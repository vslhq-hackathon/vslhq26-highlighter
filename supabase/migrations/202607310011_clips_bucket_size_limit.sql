-- Pin the clips bucket's object-size limit to the project's measured global
-- cap (50 MiB uploads succeed, 51 MiB returns 413 EntityTooLarge — the
-- Supabase default). A bucket limit can only be <= the global cap, so this is
-- a no-op today; it documents the ceiling and keeps 413 behavior deterministic
-- if the dashboard setting ever changes. Uploaders guard at cap - 1 MiB
-- (HIGHLIGHTER_MAX_UPLOAD_BYTES overrides).
update storage.buckets
set file_size_limit = 52428800
where id = 'clips';
