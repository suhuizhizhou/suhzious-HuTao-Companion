CREATE TABLE recovery_gate (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1),
 state TEXT NOT NULL CHECK(state='pending-final-sync'),
 source_snapshot_json TEXT NOT NULL,
 reconciled_at INTEGER NOT NULL);
