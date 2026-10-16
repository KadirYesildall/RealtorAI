-- Feature 002-ai-listing-launch — T057
-- Make listing-photos-original write-once against EVERY role, including the service role.
--
-- FOUND DURING IMPLEMENTATION (research R30). 0011 made originals write-once by granting
-- INSERT and SELECT policies and no UPDATE or DELETE policy. That holds for `authenticated` —
-- and for nothing else, because storage policies are row-level security and the service role
-- bypasses RLS. Verified against the running stack before writing this file, with the secret
-- key the API uses for storage:
--
--     POST   /storage/v1/object/listing-photos-original/p/a.jpg  (x-upsert: false)  -> 200
--     POST   ... same key                                         (x-upsert: true)   -> 200 overwritten
--     DELETE ... same key                                                            -> 200 deleted
--
-- So FR-001 ("immutability is a storage-layer guarantee, not an application convention") and
-- FR-065 ("the application identity must never hold delete rights on the originals bucket")
-- were both, in fact, application conventions: they held only as long as the API never called
-- the endpoints that break them.
--
-- The fix is a row trigger on storage.objects, which fires for every role regardless of RLS:
-- once an original exists, its row can be neither updated (which is how an overwrite, a move
-- or a rename is recorded) nor deleted. Storage removes an object's row before it removes the
-- bytes, so refusing the row refuses the deletion — StorageServiceTests verifies the object is
-- still retrievable after a refused delete, rather than assuming the order.
--
-- FR-065's 3-year purge (T154) is the one legitimate deletion. It is an out-of-band job with
-- its own credential and will amend this guard when it is built; until then nothing may
-- delete an original, which is the safe direction to be wrong in.

create or replace function public.reject_original_photo_mutation()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
    raise exception
        'listing-photos-original is write-once: % of "%" refused (FR-001, FR-065)',
        lower(tg_op), old.name
        using errcode = '42501',
              hint = 'Originals can never be overwritten, moved or deleted through any '
                     'application credential. Retention purging is an out-of-band job.';
end;
$$;

comment on function public.reject_original_photo_mutation() is
    'FR-001/FR-065: refuses any UPDATE or DELETE of a listing-photos-original object row, for '
    'every role including service_role, which bypasses the storage RLS policies in 0011.';

create trigger originals_reject_update
    before update on storage.objects
    for each row
    when (old.bucket_id = 'listing-photos-original')
    execute function public.reject_original_photo_mutation();

create trigger originals_reject_delete
    before delete on storage.objects
    for each row
    when (old.bucket_id = 'listing-photos-original')
    execute function public.reject_original_photo_mutation();

-- Row triggers do not fire on TRUNCATE (the lesson of research R18). service_role holds
-- TRUNCATE on storage.objects, so a statement-level guard refuses it while any original exists.
create or replace function public.reject_storage_truncate_with_originals()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
    if exists (select 1 from storage.objects where bucket_id = 'listing-photos-original') then
        raise exception 'TRUNCATE of storage.objects refused: it would destroy write-once '
                        'listing originals (FR-001, FR-065)'
            using errcode = '42501';
    end if;

    return null;
end;
$$;

create trigger originals_reject_truncate
    before truncate on storage.objects
    for each statement
    execute function public.reject_storage_truncate_with_originals();
