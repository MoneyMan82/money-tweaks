-- Image files stored in D1 (no R2 needed). Each row holds one re-encoded image or thumbnail, at most ~1.8 MB.
CREATE TABLE IF NOT EXISTS media_blobs (
    key TEXT PRIMARY KEY,
    content_type TEXT NOT NULL,
    data BLOB NOT NULL,
    bytes INTEGER NOT NULL,
    created_utc TEXT NOT NULL
);
