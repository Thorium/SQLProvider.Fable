// Hand-written Rust half of the rusqlite connector.
//
// The Fable side (Rusqlite.fs) binds to this with [<Erase; Emit>], passing only
// primitives across the boundary -- never a Fable-generated union -- so this file
// never has to know how Fable laid out `SqlValue`. Values carry the integer tags
// from `SqlValue.kind`:
//
//     0 null   1 bool   2 int   3 float   4 text   5 blob
//
// Results are materialised eagerly into RawResult. That is not laziness: a
// rusqlite `Rows` borrows the `Statement` that produced it, so a reader staying
// alive across F# calls would need a self-referential struct. Reading the whole
// set up front is also what SQLProvider's own dataReaderToArray does.
pub mod sqlite_native {
    use fable_library_rust::NativeArray_::{array_from, Array};
    use fable_library_rust::String_::{fromSlice, string};
    use rusqlite::types::{ToSql, ToSqlOutput, Value, ValueRef};
    use std::fmt;
    use rusqlite::Connection;
    use std::cell::RefCell;

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

    impl ToSql for Val {
        fn to_sql(&self) -> rusqlite::Result<ToSqlOutput<'_>> {
            let out = match self {
                Val::Null => ToSqlOutput::Owned(Value::Null),
                Val::Bool(b) => ToSqlOutput::Owned(Value::Integer(if *b { 1 } else { 0 })),
                Val::Int(i) => ToSqlOutput::Owned(Value::Integer(*i)),
                Val::Float(f) => ToSqlOutput::Owned(Value::Real(*f)),
                Val::Text(s) => ToSqlOutput::Borrowed(ValueRef::Text(s.as_bytes())),
                Val::Blob(b) => ToSqlOutput::Borrowed(ValueRef::Blob(b.as_slice())),
            };

            Ok(out)
        }
    }

    // --- parameter list ---------------------------------------------------

    /// Named parameters, accumulated one call at a time from F#.
    pub struct Params {
        items: Vec<(String, Val)>,
    }

    impl Params {
        pub fn new() -> Params {
            Params { items: Vec::new() }
        }

        fn push(&mut self, name: &str, v: Val) {
            // rusqlite matches named parameters including the marker, and the
            // portable SQL this library emits always writes `@name`.
            self.items.push((format!("@{}", name), v));
        }

        pub fn push_null(&mut self, name: string) {
            self.push(name.as_str(), Val::Null)
        }

        pub fn push_bool(&mut self, name: string, v: bool) {
            self.push(name.as_str(), Val::Bool(v))
        }

        pub fn push_int(&mut self, name: string, v: i64) {
            self.push(name.as_str(), Val::Int(v))
        }

        pub fn push_float(&mut self, name: string, v: f64) {
            self.push(name.as_str(), Val::Float(v))
        }

        pub fn push_text(&mut self, name: string, v: string) {
            self.push(name.as_str(), Val::Text(v.as_str().to_string()))
        }

        pub fn push_blob(&mut self, name: string, v: Array<u8>) {
            self.push(name.as_str(), Val::Blob(v.to_vec()));
        }

        fn as_refs(&self) -> Vec<(&str, &dyn ToSql)> {
            self.items
                .iter()
                .map(|(n, v)| (n.as_str(), v as &dyn ToSql))
                .collect()
        }
    }

    // --- results ----------------------------------------------------------

    /// A fully read result set. F# switches on `kind` before picking an accessor.
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

    pub struct Db {
        conn: Connection,
    }

    // Fable derives Debug on every generated class, and a class holding an
    // Rc<Db> only satisfies that if Db does too. rusqlite's Connection is not
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
        pub fn open(path: string) -> Db {
            let p = path.as_str();

            let conn = if p == ":memory:" {
                Connection::open_in_memory()
            } else {
                Connection::open(p)
            };

            Db {
                conn: conn.expect("SQLProvider.Fable: could not open the SQLite database"),
            }
        }

        pub fn execute(&self, sql: string, ps: &RefCell<Params>) -> i32 {
            let ps = ps.borrow();
            let refs = ps.as_refs();

            self.conn
                .execute(sql.as_str(), refs.as_slice())
                .expect("SQLProvider.Fable: execute failed") as i32
        }

        pub fn query(&self, sql: string, ps: &RefCell<Params>) -> RawResult {
            let ps = ps.borrow();
            let refs = ps.as_refs();

            let mut stmt = self
                .conn
                .prepare(sql.as_str())
                .expect("SQLProvider.Fable: prepare failed");

            let cols: Vec<String> = stmt.column_names().iter().map(|s| s.to_string()).collect();
            let width = cols.len();

            let mut rows = stmt
                .query(refs.as_slice())
                .expect("SQLProvider.Fable: query failed");

            let mut cells: Vec<Vec<Val>> = Vec::new();

            while let Some(row) = rows.next().expect("SQLProvider.Fable: row read failed") {
                let mut out: Vec<Val> = Vec::with_capacity(width);

                for i in 0..width {
                    let v = match row.get_ref(i).expect("SQLProvider.Fable: column read failed") {
                        ValueRef::Null => Val::Null,
                        ValueRef::Integer(n) => Val::Int(n),
                        ValueRef::Real(f) => Val::Float(f),
                        ValueRef::Text(t) => Val::Text(String::from_utf8_lossy(t).into_owned()),
                        ValueRef::Blob(b) => Val::Blob(b.to_vec()),
                    };

                    out.push(v);
                }

                cells.push(out);
            }

            RawResult { cols, cells }
        }
    }
}
