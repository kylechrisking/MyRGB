-- Tightening before the public beta.
-- Needs 20261020000000_supporters.sql first. Safe to run again if it stopped partway.

-- ---------------------------------------------------------------------------
-- Profiles: a display name defaults to the person's name from their sign-in, so it is only
-- public once they share a preset. Everyone else's profile stays private to them.
-- ---------------------------------------------------------------------------
drop policy if exists "profiles are readable by everyone" on public.profiles;
drop policy if exists "users read their own profile" on public.profiles;
drop policy if exists "authors of community presets are public" on public.profiles;

create policy "users read their own profile"
    on public.profiles for select using (auth.uid() = id);

create policy "authors of community presets are public"
    on public.profiles for select using (
        exists (select 1 from public.shared_presets s where s.author_id = profiles.id and not s.hidden));

-- ---------------------------------------------------------------------------
-- Supporter status is the person's own business. Only the server's own functions ask for
-- anyone else's; the badge (shows_supporter_badge) stays public because people choose to show it.
-- ---------------------------------------------------------------------------
revoke execute on function public.is_supporter(uuid) from anon, authenticated;
revoke execute on function public.cloud_limit(uuid, text) from authenticated;

-- ---------------------------------------------------------------------------
-- Install counts: one per person per preset, so the Popular list can't be pumped.
-- ---------------------------------------------------------------------------
create table if not exists public.preset_installs (
    preset_id uuid not null references public.shared_presets (id) on delete cascade,
    user_id   uuid not null references auth.users (id) on delete cascade,
    primary key (preset_id, user_id)
);

alter table public.preset_installs enable row level security; -- no policies: written by count_install only

create or replace function public.count_install(preset uuid) returns void
    language plpgsql security definer set search_path = ''
as $$
begin
    if auth.uid() is null then
        return;
    end if;
    insert into public.preset_installs (preset_id, user_id)
    select s.id, auth.uid() from public.shared_presets s where s.id = preset and not s.hidden
    on conflict do nothing;
    if found then
        update public.shared_presets set downloads = downloads + 1 where id = preset;
    end if;
end;
$$;
revoke execute on function public.count_install(uuid) from public, anon;
grant execute on function public.count_install(uuid) to authenticated;

-- ---------------------------------------------------------------------------
-- The in-app contact form is gone (the website handles contact). Keep past messages, take
-- no new ones.
-- ---------------------------------------------------------------------------
revoke insert on public.support_messages from anon, authenticated;
