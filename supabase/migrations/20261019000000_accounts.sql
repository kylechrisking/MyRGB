-- MyRGB accounts: profiles, preset sync, community presets, reports and support messages.
-- Every table has row-level security on. The desktop app only ever holds the public
-- (publishable) key plus the signed-in user's own token, so these policies are the whole
-- authorization story.

-- ---------------------------------------------------------------------------
-- Profiles: one per account, created automatically at sign-up.
-- ---------------------------------------------------------------------------
create table public.profiles (
    id           uuid primary key references auth.users (id) on delete cascade,
    display_name text not null check (char_length(display_name) between 1 and 40),
    created_at   timestamptz not null default now()
);

alter table public.profiles enable row level security;

create policy "profiles are readable by everyone"
    on public.profiles for select using (true);

create policy "users update their own profile"
    on public.profiles for update using (auth.uid() = id) with check (auth.uid() = id);

create function public.handle_new_user() returns trigger
    language plpgsql security definer set search_path = ''
as $$
begin
    insert into public.profiles (id, display_name)
    values (
        new.id,
        left(coalesce(nullif(new.raw_user_meta_data ->> 'full_name', ''),
                      nullif(new.raw_user_meta_data ->> 'name', ''),
                      split_part(new.email, '@', 1),
                      'MyRGB user'), 40)
    );
    return new;
end;
$$;

create trigger on_auth_user_created
    after insert on auth.users
    for each row execute function public.handle_new_user();

-- ---------------------------------------------------------------------------
-- Synced presets: a user's own presets, private to them.
-- ---------------------------------------------------------------------------
create table public.synced_presets (
    user_id    uuid not null references auth.users (id) on delete cascade default auth.uid(),
    preset_id  text not null check (char_length(preset_id) between 1 and 100),
    data       jsonb not null check (octet_length(data::text) <= 32768),
    deleted    boolean not null default false,
    updated_at timestamptz not null default now(),
    primary key (user_id, preset_id)
);

alter table public.synced_presets enable row level security;

create policy "users read their own presets"
    on public.synced_presets for select using (auth.uid() = user_id);
create policy "users add their own presets"
    on public.synced_presets for insert with check (auth.uid() = user_id);
create policy "users change their own presets"
    on public.synced_presets for update using (auth.uid() = user_id) with check (auth.uid() = user_id);
create policy "users remove their own presets"
    on public.synced_presets for delete using (auth.uid() = user_id);

-- ---------------------------------------------------------------------------
-- Community presets: shared publicly, owned by their author.
-- ---------------------------------------------------------------------------
create table public.shared_presets (
    id          uuid primary key default gen_random_uuid(),
    author_id   uuid not null references auth.users (id) on delete cascade default auth.uid(),
    name        text not null check (char_length(name) between 1 and 60),
    description text not null default '' check (char_length(description) <= 280),
    effect_id   text not null check (char_length(effect_id) between 1 and 60),
    data        jsonb not null check (octet_length(data::text) <= 32768),
    swatch      text[] not null default '{}' check (cardinality(swatch) <= 8),
    downloads   integer not null default 0,
    hidden      boolean not null default false, -- set by moderators, never by authors
    created_at  timestamptz not null default now()
);

create index shared_presets_popular on public.shared_presets (downloads desc) where not hidden;
create index shared_presets_newest on public.shared_presets (created_at desc) where not hidden;

alter table public.shared_presets enable row level security;

create policy "visible community presets are public"
    on public.shared_presets for select using (not hidden or auth.uid() = author_id);
create policy "signed-in users share presets as themselves"
    on public.shared_presets for insert
    with check (auth.uid() = author_id and downloads = 0 and not hidden);
create policy "authors remove their presets"
    on public.shared_presets for delete using (auth.uid() = author_id);

-- Authors may edit wording, never counters or moderation state.
revoke update on public.shared_presets from anon, authenticated;
grant update (name, description) on public.shared_presets to authenticated;
create policy "authors edit their presets"
    on public.shared_presets for update using (auth.uid() = author_id) with check (auth.uid() = author_id);

-- One public view that joins the author's display name, so the app needs one request.
create view public.community_presets with (security_invoker = true) as
    select s.id, s.name, s.description, s.effect_id, s.data, s.swatch, s.downloads, s.created_at,
           s.author_id, p.display_name as author_name
    from public.shared_presets s
    join public.profiles p on p.id = s.author_id
    where not s.hidden;

-- Counting an install. Security definer so it can bump the counter the caller can't write.
create function public.count_install(preset uuid) returns void
    language sql security definer set search_path = ''
as $$
    update public.shared_presets set downloads = downloads + 1 where id = preset and not hidden;
$$;
revoke execute on function public.count_install(uuid) from public, anon;
grant execute on function public.count_install(uuid) to authenticated;

-- ---------------------------------------------------------------------------
-- Reports: anyone signed in can flag a community preset for review.
-- ---------------------------------------------------------------------------
create table public.preset_reports (
    id          bigint generated always as identity primary key,
    preset_id   uuid not null references public.shared_presets (id) on delete cascade,
    reporter_id uuid not null references auth.users (id) on delete cascade default auth.uid(),
    reason      text not null check (char_length(reason) between 1 and 500),
    created_at  timestamptz not null default now(),
    unique (preset_id, reporter_id)
);

alter table public.preset_reports enable row level security;

create policy "signed-in users report as themselves"
    on public.preset_reports for insert with check (auth.uid() = reporter_id);

-- ---------------------------------------------------------------------------
-- Support messages: contacting Root Labs requires an account. Write-only for users;
-- the team reads them in the Supabase dashboard.
-- ---------------------------------------------------------------------------
create table public.support_messages (
    id          bigint generated always as identity primary key,
    user_id     uuid not null references auth.users (id) on delete cascade default auth.uid(),
    subject     text not null check (char_length(subject) between 1 and 120),
    body        text not null check (char_length(body) between 1 and 5000),
    app_version text not null default '' check (char_length(app_version) <= 40),
    created_at  timestamptz not null default now()
);

alter table public.support_messages enable row level security;

create policy "signed-in users send messages as themselves"
    on public.support_messages for insert with check (auth.uid() = user_id);

-- At most 5 messages per user per day, so the form can't be used to flood the inbox.
create function public.limit_support_messages() returns trigger
    language plpgsql security definer set search_path = ''
as $$
begin
    if (select count(*) from public.support_messages
        where user_id = new.user_id and created_at > now() - interval '1 day') >= 5 then
        raise exception 'Too many messages today. Please try again tomorrow.';
    end if;
    return new;
end;
$$;

create trigger support_messages_rate_limit
    before insert on public.support_messages
    for each row execute function public.limit_support_messages();

-- ---------------------------------------------------------------------------
-- Account deletion, from inside the app. Removes the auth user; everything above
-- cascades with it.
-- ---------------------------------------------------------------------------
create function public.delete_my_account() returns void
    language plpgsql security definer set search_path = ''
as $$
begin
    if auth.uid() is null then
        raise exception 'Not signed in.';
    end if;
    delete from auth.users where id = auth.uid();
end;
$$;
revoke execute on function public.delete_my_account() from public, anon;
grant execute on function public.delete_my_account() to authenticated;
