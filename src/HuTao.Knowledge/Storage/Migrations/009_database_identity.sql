CREATE TABLE database_identity (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1),
 lineage_id TEXT NOT NULL CHECK(length(lineage_id)=32 AND lineage_id NOT GLOB '*[^0-9a-f]*'));
INSERT INTO database_identity VALUES(1,lower(hex(randomblob(16))));
CREATE TRIGGER database_identity_no_update BEFORE UPDATE ON database_identity
 BEGIN SELECT RAISE(ABORT,'Database lineage is immutable'); END;
CREATE TRIGGER database_identity_no_delete BEFORE DELETE ON database_identity
 BEGIN SELECT RAISE(ABORT,'Database lineage is immutable'); END;
