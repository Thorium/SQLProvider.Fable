// Hand-written Rust half of the sqlx connector.
//
// The Fable side (Sqlx.fs) binds to this with [<Erase; Emit>], passing only
// primitives across the boundary -- never a Fable-generated union -- so this
// file never has to know how Fable laid out `SqlValue`. Values carry the integer
// tags from `SqlValue.kind`:
//
//     0 null   1 bool   2 int   3 float   4 text   5 blob
//
// One shim covers SQLite, PostgreSQL and MySQL/MariaDB because it goes through
// sqlx's `Any` driver, which picks the backend from the URL scheme at runtime.
// What `Any` does not normalise is placeholder syntax, so the SQL arriving here
// has already been rewritten by `Dialect.bind` on the F# side and every
// parameter is positional.
//
// Results are materialised eagerly into RawResult. That is not laziness: a sqlx
// row stream borrows the connection that produced it, so a reader staying alive
// across F# calls would need a self-referential struct. Reading the whole set up
// front is also what SQLProvider's own dataReaderToArray does.
pub mod sqlx_native {
    use fable_library_rust::Async_::Async;
    use fable_library_rust::NativeArray_::{array_from, Array};
    use fable_library_rust::String_::{fromSlice, string};

    use futures::channel::oneshot;
    use sqlx::any::{AnyArguments, AnyTypeInfoKind};
    use sqlx::query::Query;
    use sqlx::{Any, AnyConnection, Column, Connection, Executor, Row, ValueRef};
    use std::fmt;
    use std::future::Future;
    use std::sync::{Arc, Mutex, OnceLock};
    use tokio::runtime::Runtime;
    use tokio::sync::Mutex as ConnMutex;

    // --- the runtime ------------------------------------------------------

    static RUNTIME: OnceLock<Runtime> = OnceLock::new();

    /// The tokio runtime every query actually runs on. sqlx needs one, and
    /// Fable's own executor (a `futures` thread pool) is not one, so the two
    /// live side by side and are joined by the oneshot channel in `bridge`.
    fn runtime() -> &'static Runtime {
        RUNTIME.get_or_init(|| {
            // Registers whichever of the sqlite/postgres/mysql drivers were
            // compiled in. Connecting without this panics.
            sqlx::any::install_default_drivers();

            tokio::runtime::Builder::new_multi_thread()
                .enable_all()
                .build()
                .expect("SQLProvider.Fable: could not start the tokio runtime")
        })
    }

    /// Hands a sqlx future to tokio and gives F# back an `Async` that completes
    /// when it does.
    ///
    /// The work is spawned rather than blocked on, so the caller's thread is
    /// free while the database is busy -- which is the whole reason
    /// ISqlConnector is async. Only the oneshot receiver crosses into the Fable
    /// future; the sqlx future itself never leaves tokio, which also keeps
    /// `Async`'s `Sync` bound off it.
    fn bridge<T, F>(fut: F) -> Arc<Async<T>>
    where
        T: Clone + Send + Sync + 'static,
        F: Future<Output = Result<T, String>> + Send + 'static,
    {
        let (tx, rx) = oneshot::channel::<Result<T, String>>();

        runtime().spawn(async move {
            // The receiver is dropped only if F# discarded the Async, in which
            // case nobody is waiting for this and the send failing is correct.
            let _ = tx.send(fut.await);
        });

        // A database error travels across as a message and is raised here, on
        // whichever thread is awaiting the Async, rather than panicking inside
        // the tokio task. That matters twice over: F#'s `try/with` can only
        // catch a panic on its own thread, and a panic on the worker would
        // drop the sender and surface as "the task was dropped" instead of the
        // error the engine actually reported.
        Async::from_future(async move {
            match rx.await {
                Ok(Ok(value)) => value,
                Ok(Err(message)) => panic!("{}", message),
                Err(_) => panic!("SQLProvider.Fable: the database task was dropped before it completed"),
            }
        })
    }

    // --- values -----------------------------------------------------------

    #[derive(Clone)]
    pub enum Val {
        Null,
        Bool(bool),
        Int(i64),
        Float(f64),
        Text(String),
        Blob(Vec<u8>),
    }

    /// Reads one cell out of a row.
    ///
    /// sqlx's Any driver normalises every backend's types down to nine kinds, so
    /// the kind decides which decode to ask for. The integer widths are then
    /// collapsed -- a SMALLINT, INTEGER and BIGINT column all read back as
    /// SqlInt -- because the F# side has one integer case and the vendors
    /// disagree about which width a given column type produces.
    ///
    /// A failure is an `Err`, never a panic: this runs inside the spawned tokio
    /// task, where a panic drops the oneshot sender and all F# would see is
    /// "the database task was dropped" -- the same masking `bridge` exists to
    /// prevent. A column the Any driver has no kind for (a PostgreSQL NUMERIC,
    /// say) comes through here, and its error should name the column.
    fn read_cell(row: &sqlx::any::AnyRow, i: usize) -> Result<Val, String> {
        let describe = |what: &str, e: &dyn fmt::Display| {
            let name = row
                .columns()
                .get(i)
                .map(|c| c.name().to_string())
                .unwrap_or_else(|| i.to_string());

            format!("SQLProvider.Fable: could not {} column {}: {}", what, name, e)
        };

        let (kind, is_null) = {
            let raw = match row.try_get_raw(i) {
                Ok(raw) => raw,
                Err(e) => return Err(describe("read", &e)),
            };

            (raw.type_info().kind(), raw.is_null())
        };

        if is_null {
            return Ok(Val::Null);
        }

        let decoded = match kind {
            AnyTypeInfoKind::Null => Ok(Val::Null),
            AnyTypeInfoKind::Bool => row.try_get::<bool, _>(i).map(Val::Bool),
            AnyTypeInfoKind::SmallInt => row.try_get::<i16, _>(i).map(|v| Val::Int(v as i64)),
            AnyTypeInfoKind::Integer => row.try_get::<i32, _>(i).map(|v| Val::Int(v as i64)),
            AnyTypeInfoKind::BigInt => row.try_get::<i64, _>(i).map(Val::Int),
            AnyTypeInfoKind::Real => row.try_get::<f32, _>(i).map(|v| Val::Float(v as f64)),
            AnyTypeInfoKind::Double => row.try_get::<f64, _>(i).map(Val::Float),
            AnyTypeInfoKind::Text => row.try_get::<String, _>(i).map(Val::Text),
            AnyTypeInfoKind::Blob => row.try_get::<Vec<u8>, _>(i).map(Val::Blob),
        };

        decoded.map_err(|e| describe("decode", &e))
    }

    // --- parameter list ---------------------------------------------------

    /// Positional parameters, accumulated one call at a time from F#. There are
    /// no names: `Dialect.bind` has already replaced every `@name` with the
    /// marker this backend wants and put the values in the matching order.
    pub struct Params {
        items: Vec<Val>,
    }

    impl Params {
        pub fn new() -> Params {
            Params { items: Vec::new() }
        }

        pub fn push_null(&mut self) {
            self.items.push(Val::Null)
        }

        pub fn push_bool(&mut self, v: bool) {
            self.items.push(Val::Bool(v))
        }

        pub fn push_int(&mut self, v: i64) {
            self.items.push(Val::Int(v))
        }

        pub fn push_float(&mut self, v: f64) {
            self.items.push(Val::Float(v))
        }

        pub fn push_text(&mut self, v: string) {
            self.items.push(Val::Text(v.as_str().to_string()))
        }

        pub fn push_blob(&mut self, v: Array<u8>) {
            self.items.push(Val::Blob(v.to_vec()))
        }
    }

    fn bind_all<'q>(
        mut q: Query<'q, Any, AnyArguments<'q>>,
        items: &[Val],
    ) -> Query<'q, Any, AnyArguments<'q>> {
        for v in items {
            q = match v {
                // A NULL still has to be given some type for the wire format.
                // Text is the widest choice the Any driver offers and matches
                // what the ADO backend sends.
                Val::Null => q.bind(Option::<String>::None),
                Val::Bool(b) => q.bind(*b),
                Val::Int(i) => q.bind(*i),
                Val::Float(f) => q.bind(*f),
                Val::Text(s) => q.bind(s.clone()),
                Val::Blob(b) => q.bind(b.clone()),
            };
        }

        q
    }

    // --- results ----------------------------------------------------------

    /// A fully read result set. F# switches on `kind` before picking an
    /// accessor.
    pub struct RawResult {
        cols: Vec<String>,
        cells: Vec<Vec<Val>>,
    }

    impl RawResult {
        pub fn col_count(&self) -> i32 {
            self.cols.len() as i32
        }

        pub fn row_count(&self) -> i32 {
            self.cells.len() as i32
        }

        pub fn col_name(&self, i: i32) -> string {
            fromSlice(self.cols[i as usize].as_str())
        }

        fn at(&self, r: i32, c: i32) -> &Val {
            &self.cells[r as usize][c as usize]
        }

        pub fn kind(&self, r: i32, c: i32) -> i32 {
            match self.at(r, c) {
                Val::Null => 0,
                Val::Bool(_) => 1,
                Val::Int(_) => 2,
                Val::Float(_) => 3,
                Val::Text(_) => 4,
                Val::Blob(_) => 5,
            }
        }

        pub fn get_bool(&self, r: i32, c: i32) -> bool {
            match self.at(r, c) {
                Val::Bool(b) => *b,
                Val::Int(i) => *i != 0,
                _ => false,
            }
        }

        pub fn get_int(&self, r: i32, c: i32) -> i64 {
            match self.at(r, c) {
                Val::Int(i) => *i,
                Val::Bool(b) => *b as i64,
                _ => 0,
            }
        }

        pub fn get_float(&self, r: i32, c: i32) -> f64 {
            match self.at(r, c) {
                Val::Float(f) => *f,
                Val::Int(i) => *i as f64,
                _ => 0.0,
            }
        }

        pub fn get_text(&self, r: i32, c: i32) -> string {
            match self.at(r, c) {
                Val::Text(s) => fromSlice(s.as_str()),
                _ => fromSlice(""),
            }
        }

        pub fn get_blob(&self, r: i32, c: i32) -> Array<u8> {
            match self.at(r, c) {
                Val::Blob(b) => array_from(b.clone()),
                _ => array_from(Vec::new()),
            }
        }
    }

    // --- connection -------------------------------------------------------

    /// One connection, not a pool.
    ///
    /// A pool would hand successive statements to different connections, which
    /// breaks `BEGIN`/`COMMIT` (they are plain statements here) and, for
    /// `sqlite::memory:`, would give each connection its own empty database.
    pub struct Db {
        conn: Arc<ConnMutex<AnyConnection>>,
    }

    // Fable derives Debug on every generated class, and a class holding an
    // Arc<Db> only satisfies that if Db does too. sqlx's AnyConnection is not
    // Debug, so these print an opaque placeholder rather than any contents.
    impl fmt::Debug for Db {
        fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
            f.write_str("Db")
        }
    }

    impl fmt::Debug for Params {
        fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
            write!(f, "Params({})", self.items.len())
        }
    }

    impl fmt::Debug for RawResult {
        fn fmt(&self, f: &mut fmt::Formatter) -> fmt::Result {
            write!(f, "RawResult({}x{})", self.cells.len(), self.cols.len())
        }
    }

    impl Db {
        /// Connects, blocking. This is the one blocking call in the file: it
        /// happens once, in a constructor, where F# has nothing to await with.
        pub fn open(url: string) -> Db {
            let target = url.as_str().to_string();

            let conn = runtime()
                .block_on(async move { AnyConnection::connect(target.as_str()).await })
                .expect("SQLProvider.Fable: could not open the database");

            Db {
                conn: Arc::new(ConnMutex::new(conn)),
            }
        }

        pub fn query(&self, sql: string, ps: &Mutex<Params>) -> Arc<Async<Arc<RawResult>>> {
            let conn = self.conn.clone();
            let sql = sql.as_str().to_string();
            let items = ps.lock().unwrap().items.clone();

            bridge(async move {
                let mut guard = conn.lock().await;

                let rows = match bind_all(sqlx::query(sql.as_str()), items.as_slice())
                    .fetch_all(&mut *guard)
                    .await
                {
                    Ok(rows) => rows,
                    Err(e) => return Err(format!("SQLProvider.Fable: query failed: {}", e)),
                };

                let cols: Vec<String> = match rows.first() {
                    Some(row) => row.columns().iter().map(|c| c.name().to_string()).collect(),
                    None => Vec::new(),
                };

                let mut cells: Vec<Vec<Val>> = Vec::with_capacity(rows.len());

                for row in rows.iter() {
                    let mut out: Vec<Val> = Vec::with_capacity(cols.len());

                    for i in 0..cols.len() {
                        out.push(read_cell(row, i)?);
                    }

                    cells.push(out);
                }

                Ok(Arc::new(RawResult { cols, cells }))
            })
        }

        pub fn execute(&self, sql: string, ps: &Mutex<Params>) -> Arc<Async<i32>> {
            let conn = self.conn.clone();
            let sql = sql.as_str().to_string();
            let items = ps.lock().unwrap().items.clone();

            bridge(async move {
                let mut guard = conn.lock().await;

                match bind_all(sqlx::query(sql.as_str()), items.as_slice())
                    .execute(&mut *guard)
                    .await
                {
                    Ok(result) => Ok(result.rows_affected() as i32),
                    Err(e) => Err(format!("SQLProvider.Fable: execute failed: {}", e)),
                }
            })
        }
    }
}
