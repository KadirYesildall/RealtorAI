# Retention purge (FR-065)

Deletes original photos whose listing was deleted longer ago than the retention period, and logs
each purge as an `originals_purged` audit event. It is the only way an original is ever deleted.

This is an operations job, not part of the API. It runs with its own database credential,
`retention_purger`, which migration `0019_retention_purge.sql` creates **without a login**.

## Setting it up in an environment

1. Grant the role a login, out of band, with a password from your secret store:

   ```sql
   alter role retention_purger login password '<from the secret store>';
   ```

2. Schedule the job (daily is enough) with:

   | Variable | Value |
   |---|---|
   | `RETENTION_PURGER_CONNECTION` | A Postgres connection string for `retention_purger` |
   | `STORAGE_API_URL` | The Storage API, e.g. `https://<project>.supabase.co/storage/v1` |
   | `STORAGE_KEY` | A key the Storage API accepts |
   | `ORIGINALS_BUCKET` | Optional; default `listing-photos-original` |

3. Run `dotnet run --project RealtorAI_BackEnd/ops/retention-purge -- --dry-run` first to see
   what is eligible without deleting anything.

## The retention period

`ops.retention_settings`, key `originals_after_listing_deleted` — 3 years by default. Its
legally required value is confirmed in the AB 723 review (research R4). Changing it is a direct,
reviewed database change; no application path writes it.

## Why the storage key cannot delete on its own

Deleting through the Storage API is what removes the bytes, and the Storage API acts with the
same role as the application. So the write-once guard on `storage.objects` lets a deletion through
only when `retention_purger` has written an authorization for that object **and** its listing is
still past the retention period at that moment. The application's roles have no privilege on the
`ops` schema and cannot write an authorization. See research R38.
