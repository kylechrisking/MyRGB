-- Support recorded by the Buy Me a Coffee webhook (supabase/functions/bmc-webhook) is its own
-- source, alongside Ko-fi and GitHub. Claims keep the same value; that column has no check.

alter table public.supporters drop constraint if exists supporters_source_check;
alter table public.supporters add constraint supporters_source_check
    check (source in ('manual', 'kofi', 'github', 'bmc'));
