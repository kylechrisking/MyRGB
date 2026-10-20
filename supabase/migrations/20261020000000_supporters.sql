-- Supporters: people who fund MyRGB get a badge on what they share and higher cloud limits.
-- Every feature of the app stays free; only the cloud, which costs money to run, has limits.
--
-- Support is recorded by the server only: the Ko-fi webhook (supabase/functions/kofi-webhook)
-- or the team in the dashboard. No policy lets a user write these tables.

-- ---------------------------------------------------------------------------
-- Profiles: users may change their name and badge preference, nothing else.
-- ---------------------------------------------------------------------------
alter table public.profiles add column show_supporter_badge boolean not null default true;

revoke update on public.profiles from anon, authenticated;
grant update (display_name, show_supporter_badge) on public.profiles to authenticated;

-- ---------------------------------------------------------------------------
-- Supporters. until = null means no end date (set by hand); monthly support sets it ahead.
-- ---------------------------------------------------------------------------
create table public.supporters (
    user_id   uuid primary key references auth.users (id) on delete cascade,
    since     timestamptz not null default now(),
    until     timestamptz,
    source    text not null default 'manual' check (source in ('manual', 'kofi', 'github')),
    reference text check (char_length(reference) <= 200) -- payment id, for refunds and disputes
);

alter table public.supporters enable row level security;

create policy "users see their own support"
    on public.supporters for select using (auth.uid() = user_id);

-- Support that arrived before its account existed (Ko-fi email with no MyRGB account yet).
-- Claimed automatically when an account with that email signs up.
create table public.supporter_claims (
    email      text primary key check (email = lower(email)),
    until      timestamptz,
    source     text not null default 'kofi',
    reference  text check (char_length(reference) <= 200),
    created_at timestamptz not null default now()
);

alter table public.supporter_claims enable row level security; -- no policies: server only

create function public.is_supporter(uid uuid) returns boolean
    language sql stable security definer set search_path = ''
as $$
    select exists (select 1 from public.supporters s where s.user_id = uid and (s.until is null or s.until > now()));
$$;
revoke execute on function public.is_supporter(uuid) from public;
grant execute on function public.is_supporter(uuid) to anon, authenticated;

-- The badge others see: a current supporter who hasn't hidden it.
create function public.shows_supporter_badge(uid uuid) returns boolean
    language sql stable security definer set search_path = ''
as $$
    select public.is_supporter(uid)
       and coalesce((select p.show_supporter_badge from public.profiles p where p.id = uid), false);
$$;
revoke execute on function public.shows_supporter_badge(uuid) from public;
grant execute on function public.shows_supporter_badge(uuid) to anon, authenticated;

-- New accounts pick up support that was waiting for them.
create or replace function public.handle_new_user() returns trigger
    language plpgsql security definer set search_path = ''
as $$
declare
    claim public.supporter_claims;
begin
    insert into public.profiles (id, display_name)
    values (
        new.id,
        left(coalesce(nullif(new.raw_user_meta_data ->> 'full_name', ''),
                      nullif(new.raw_user_meta_data ->> 'name', ''),
                      split_part(new.email, '@', 1),
                      'MyRGB user'), 40)
    );

    select * into claim from public.supporter_claims where email = lower(new.email);
    if found then
        insert into public.supporters (user_id, until, source, reference)
        values (new.id, claim.until, claim.source, claim.reference);
        delete from public.supporter_claims where email = claim.email;
    end if;
    return new;
end;
$$;

-- ---------------------------------------------------------------------------
-- Cloud limits. Generous for everyone; ten times that for supporters.
-- ---------------------------------------------------------------------------
create function public.cloud_limit(uid uuid, what text) returns integer
    language sql stable security definer set search_path = ''
as $$
    select case what
        when 'synced' then case when public.is_supporter(uid) then 1000 else 100 end
        when 'shared' then case when public.is_supporter(uid) then 200 else 20 end
    end;
$$;
revoke execute on function public.cloud_limit(uuid, text) from public;
grant execute on function public.cloud_limit(uuid, text) to authenticated;

create function public.limit_synced_presets() returns trigger
    language plpgsql security definer set search_path = ''
as $$
declare
    cap integer := public.cloud_limit(new.user_id, 'synced');
begin
    if not new.deleted and (select count(*) from public.synced_presets
                            where user_id = new.user_id and not deleted and preset_id <> new.preset_id) >= cap then
        raise exception 'You can sync up to % presets. Supporters can sync 1000.', cap
            using errcode = 'P0001', hint = 'limit';
    end if;
    return new;
end;
$$;

create trigger synced_presets_limit
    before insert or update on public.synced_presets
    for each row execute function public.limit_synced_presets();

create function public.limit_shared_presets() returns trigger
    language plpgsql security definer set search_path = ''
as $$
declare
    cap integer := public.cloud_limit(new.author_id, 'shared');
begin
    if (select count(*) from public.shared_presets where author_id = new.author_id) >= cap then
        raise exception 'You can share up to % presets. Supporters can share 200.', cap
            using errcode = 'P0001', hint = 'limit';
    end if;
    return new;
end;
$$;

create trigger shared_presets_limit
    before insert on public.shared_presets
    for each row execute function public.limit_shared_presets();

-- ---------------------------------------------------------------------------
-- The community view gains the author's badge.
-- ---------------------------------------------------------------------------
create or replace view public.community_presets with (security_invoker = true) as
    select s.id, s.name, s.description, s.effect_id, s.data, s.swatch, s.downloads, s.created_at,
           s.author_id, p.display_name as author_name,
           public.shows_supporter_badge(s.author_id) as author_supporter
    from public.shared_presets s
    join public.profiles p on p.id = s.author_id
    where not s.hidden;
