-- Durable studio-agent chat. One row per message, keyed by project + context
-- ("long" today; "short" reserved) — deliberately NOT by long-form version, so
-- a finished revision no longer wipes the conversation. The API appends a
-- completion row (job_final = true) when a chat-started job ends, so a user
-- who left mid-revision finds the outcome already in the chat.

create table public.agent_messages (
  id uuid primary key default gen_random_uuid(),
  project_id uuid not null references public.projects(id) on delete cascade,
  context text not null default 'long' check (context in ('long', 'short')),
  role text not null check (role in ('user', 'agent')),
  text text not null,
  job_id text,
  job_final boolean not null default false,
  created_at timestamptz not null default now()
);

create index agent_messages_project_created
  on public.agent_messages (project_id, created_at);

comment on table public.agent_messages is
  'Studio editing-agent chat transcript, durable across sessions. job_id marks a message that started a pipeline job; job_final marks the server-written completion message for that job.';

-- Writes go through the API (service_role). Users can read their own rows.
alter table public.agent_messages enable row level security;
alter table public.agent_messages force row level security;

revoke all on table public.agent_messages from anon, authenticated;
grant all on table public.agent_messages to service_role;
grant select on table public.agent_messages to authenticated;

create policy agent_messages_owner_select on public.agent_messages
  for select to authenticated
  using (exists (
    select 1 from public.projects p
    where p.id = project_id and p.user_id = auth.uid()
  ));
