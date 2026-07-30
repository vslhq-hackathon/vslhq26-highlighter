-- Clips now ship a padded "editing master" render (clip window ±handle
-- seconds) so the studio editor can extend past the LLM's cut points; teach
-- the delete trigger about the extra storage object.

create or replace function public.enqueue_clip_media_cleanup()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
  render jsonb := old.metadata -> 'render';
  object_key text;
begin
  if render ->> 'bucket' is null then
    return old;
  end if;
  foreach object_key in array array[
    render ->> 'storage_path',
    render ->> 'thumbnail_storage_path',
    render ->> 'vertical_storage_path',
    render ->> 'captioned_storage_path',
    render ->> 'square_storage_path',
    render ->> 'square_captioned_storage_path',
    render ->> 'master_storage_path'
  ]
  loop
    if object_key is not null then
      insert into public.media_cleanup_jobs (origin_table, origin_id, store, bucket, object_key)
      values ('clips', old.id::text, 'supabase_storage', render ->> 'bucket', object_key);
    end if;
  end loop;
  return old;
end;
$$;
