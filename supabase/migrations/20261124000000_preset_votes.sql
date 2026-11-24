-- Community votes: an up or down vote per person per shared preset, and an optional "suggest this
-- for MyRGB's built-in presets" nomination. Additive only: nothing existing changes shape except
-- the community view, which gains the two counts.

alter table public.shared_presets
    add column if not exists score       integer not null default 0,
    add column if not exists nominations integer not null default 0;

create index if not exists shared_presets_score on public.shared_presets (score desc, downloads desc) where not hidden;

create table if not exists public.preset_votes (
    preset_id  uuid not null references public.shared_presets (id) on delete cascade,
    user_id    uuid not null references auth.users (id) on delete cascade default auth.uid(),
    vote       smallint not null default 0 check (vote between -1 and 1),
    nominate   boolean not null default false,
    updated_at timestamptz not null default now(),
    primary key (preset_id, user_id)
);

alter table public.preset_votes enable row level security;

-- People see only their own votes (to show which arrow is lit); writes go through the functions
-- below, which keep the counters on shared_presets in step.
create policy "users read their own votes"
    on public.preset_votes for select using (auth.uid() = user_id);
revoke insert, update, delete on public.preset_votes from anon, authenticated;

-- Set the caller's vote (-1, 0 or 1) and nomination on one preset. Authors can't vote on their own.
create or replace function public.vote_preset(preset uuid, value integer, nominated boolean) returns void
    language plpgsql security definer set search_path = ''
as $$
declare
    old_vote smallint := 0;
    old_nom  boolean  := false;
begin
    if auth.uid() is null or value not between -1 and 1 then
        return;
    end if;
    if not exists (select 1 from public.shared_presets s where s.id = preset and not s.hidden and s.author_id <> auth.uid()) then
        return;
    end if;

    select v.vote, v.nominate into old_vote, old_nom
        from public.preset_votes v where v.preset_id = preset and v.user_id = auth.uid() for update;
    old_vote := coalesce(old_vote, 0);
    old_nom := coalesce(old_nom, false);

    insert into public.preset_votes (preset_id, user_id, vote, nominate, updated_at)
        values (preset, auth.uid(), value, nominated, now())
        on conflict (preset_id, user_id) do update set vote = excluded.vote, nominate = excluded.nominate, updated_at = now();

    update public.shared_presets
        set score = score + (value - old_vote),
            nominations = nominations + (nominated::int - old_nom::int)
        where id = preset;
end;
$$;
revoke execute on function public.vote_preset(uuid, integer, boolean) from public, anon;
grant execute on function public.vote_preset(uuid, integer, boolean) to authenticated;

-- A vote that disappears (its account was deleted) takes its share of the counters with it.
create or replace function public.preset_vote_removed() returns trigger
    language plpgsql security definer set search_path = ''
as $$
begin
    update public.shared_presets
        set score = score - old.vote, nominations = nominations - old.nominate::int
        where id = old.preset_id;
    return old;
end;
$$;
revoke execute on function public.preset_vote_removed() from public, anon, authenticated;

drop trigger if exists preset_votes_removed on public.preset_votes;
create trigger preset_votes_removed
    after delete on public.preset_votes
    for each row execute function public.preset_vote_removed();

-- The community view gains the counts. Same columns as before, two added at the end.
create or replace view public.community_presets with (security_invoker = true) as
    select s.id, s.name, s.description, s.effect_id, s.data, s.swatch, s.downloads, s.created_at,
           s.author_id, p.display_name as author_name,
           public.shows_supporter_badge(s.author_id) as author_supporter,
           s.score, s.nominations
    from public.shared_presets s
    join public.profiles p on p.id = s.author_id
    where not s.hidden;
