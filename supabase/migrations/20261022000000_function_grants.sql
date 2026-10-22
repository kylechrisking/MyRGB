-- Postgres lets everyone (PUBLIC) execute new functions. Take that back from the ones that only
-- the database itself calls: trigger functions and the helpers inside them. Triggers keep working;
-- they don't need the caller to hold EXECUTE.
revoke execute on function public.handle_new_user() from public, anon, authenticated;
revoke execute on function public.limit_synced_presets() from public, anon, authenticated;
revoke execute on function public.limit_shared_presets() from public, anon, authenticated;
revoke execute on function public.limit_support_messages() from public, anon, authenticated;
revoke execute on function public.cloud_limit(uuid, text) from public, anon, authenticated;
revoke execute on function public.is_supporter(uuid) from public, anon, authenticated;

-- Callable on purpose, and each only ever acts on the caller or on public data:
--   count_install(uuid)          signed in; counts the caller's own install once
--   delete_my_account()          signed in; deletes only the caller
--   shows_supporter_badge(uuid)  everyone; the community view uses it, and it is true only for
--                                supporters who chose to show their badge
