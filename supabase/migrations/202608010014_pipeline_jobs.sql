-- Durable pipeline job records: the queue-dispatch bookkeeping that lets ingest
-- run on remote Container Apps Job workers and lets any API replica answer
-- /api/jobs reads. The Azure Storage queue carries the work signal (KEDA scales
-- on it); this table is the source of truth for job state. Locally-spawned jobs
-- (revise, publish, editor exports) are mirrored here best-effort so state
-- reads survive an API restart or land correctly on another replica.

create table public.pipeline_jobs (
  id text primary key check (id ~ '^job_[0-9a-f]{12}$'),
  kind text not null,
  project_id uuid references public.projects(id) on delete cascade,
  owner_id uuid,
  argv jsonb not null default '[]'::jsonb,
  -- pending: enqueued, no worker yet. cancel_requested: force-cancel asked;
  -- the worker wrapper polls for it and converges to killed.
  status text not null default 'pending' check (status in
    ('pending', 'running', 'succeeded', 'failed', 'killed', 'cancel_requested')),
  exit_code int,
  error text,
  -- File name under outputs/api/jobs on the shared Azure Files mount; the API
  -- serves log tails and SSE streams from it for jobs it didn't spawn itself.
  log_name text,
  worker text,
  created_at timestamptz not null default now(),
  started_at timestamptz,
  ended_at timestamptz
);

create index pipeline_jobs_project on public.pipeline_jobs (project_id, created_at desc);
create index pipeline_jobs_status on public.pipeline_jobs (status) where status in ('pending', 'running', 'cancel_requested');

comment on table public.pipeline_jobs is
  'Durable job queue/state records. Ingest rows are claimed by Container Apps Job workers via an Azure Storage queue message carrying the job id; other kinds are mirrors of API-local subprocess jobs.';

-- Writes go through the API / worker (service_role). Users can read their own.
alter table public.pipeline_jobs enable row level security;
alter table public.pipeline_jobs force row level security;

revoke all on table public.pipeline_jobs from anon, authenticated;
grant all on table public.pipeline_jobs to service_role;
grant select on table public.pipeline_jobs to authenticated;

create policy pipeline_jobs_owner_select on public.pipeline_jobs
  for select to authenticated
  using (owner_id = auth.uid());
