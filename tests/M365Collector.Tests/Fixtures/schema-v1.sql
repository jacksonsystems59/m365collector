CREATE TABLE SchemaVersion(Version INTEGER NOT NULL);
CREATE TABLE Settings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
                CREATE TABLE Users(Name TEXT PRIMARY KEY COLLATE NOCASE, Hash TEXT NOT NULL, Role INTEGER NOT NULL CHECK(Role BETWEEN 0 AND 2), Failures INTEGER NOT NULL DEFAULT 0, LockedUntil TEXT);
                CREATE TABLE Customers(TenantId TEXT PRIMARY KEY, Name TEXT NOT NULL, Payload TEXT NOT NULL);
                CREATE TABLE ModuleConfiguration(TenantId TEXT NOT NULL REFERENCES Customers(TenantId), ModuleId TEXT NOT NULL, Enabled INTEGER NOT NULL, PRIMARY KEY(TenantId,ModuleId));
                CREATE TABLE CollectionState(TenantId TEXT NOT NULL REFERENCES Customers(TenantId), ModuleId TEXT NOT NULL, LastRun TEXT, Status TEXT NOT NULL, PRIMARY KEY(TenantId,ModuleId));
                CREATE TABLE ConnectionRequests(Id TEXT PRIMARY KEY, Payload TEXT NOT NULL, State TEXT NOT NULL, Error TEXT, Created TEXT NOT NULL);
                CREATE TABLE UpdateHistory(Id INTEGER PRIMARY KEY, Time TEXT NOT NULL, Version TEXT NOT NULL, Result TEXT NOT NULL);
                INSERT INTO SchemaVersion VALUES(1);
