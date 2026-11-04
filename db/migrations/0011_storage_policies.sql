-- =============================================================================
-- 0011_storage_policies.sql
-- Supabase Storage: a private bucket for lease documents, with policies that
-- confine every object to its owning organization (section 4.B, 4.C).
--
-- Access model
--   Upload   Browser -> API. The API buffers the file, checks the real file
--            signature, size and page count, THEN writes it to Storage with
--            the service-role key under a server-generated path. There is no
--            client INSERT policy, so a browser cannot place an object in the
--            bucket at all.
--   Download Browser asks the API, which authorizes the request and returns a
--            short-lived signed URL. No public bucket, no long-lived link.
--   Delete   API only, as part of the retention sweep.
--
-- The SELECT policy below is therefore defence in depth rather than the
-- primary path: if a direct-from-browser read is ever introduced, it is
-- already confined to the caller's own organization.
--
-- This migration is a no-op on a plain PostgreSQL instance (local development
-- without the Supabase Storage extension), so the same migration set runs in
-- both places.
-- =============================================================================

do $storage$
declare
  v_bucket text := 'lease-documents';
begin
  if not exists (
    select 1 from information_schema.tables
    where table_schema = 'storage' and table_name = 'buckets'
  ) then
    raise notice
      'Supabase Storage schema not present; skipping bucket and policy setup. '
      'This is expected on a plain PostgreSQL instance.';
    return;
  end if;

  -- ---------------------------------------------------------------------------
  -- The bucket. public = false is the point of the whole exercise: leases are
  -- never served from a public URL.
  -- ---------------------------------------------------------------------------
  insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
  values (
    v_bucket,
    v_bucket,
    false,
    52428800,  -- 50 MB, matching the lease_documents CHECK constraint
    array[
      'application/pdf',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      'image/jpeg',
      'image/png'
    ]
  )
  on conflict (id) do update
    set public             = false,
        file_size_limit    = excluded.file_size_limit,
        allowed_mime_types = excluded.allowed_mime_types;

  -- ---------------------------------------------------------------------------
  -- Policies. Dropped first so this migration is idempotent.
  -- ---------------------------------------------------------------------------
  drop policy if exists lease_documents_read_own_org on storage.objects;
  drop policy if exists lease_documents_no_client_insert on storage.objects;
  drop policy if exists lease_documents_no_client_update on storage.objects;
  drop policy if exists lease_documents_no_client_delete on storage.objects;

  -- A signed-in user may read an object only when the organization segment of
  -- its path is an organization they belong to. The path shape is guaranteed
  -- by the CHECK constraints on lease_documents, so segment 2 is always the
  -- owning organization id.
  --
  --   org / <organization uuid> / documents / <object uuid>.<ext>
  --    1            2                3                4
  execute format($policy$
    create policy lease_documents_read_own_org on storage.objects
      for select
      to authenticated
      using (
        bucket_id = %L
        and (storage.foldername(name))[1] = 'org'
        and (storage.foldername(name))[3] = 'documents'
        and app.is_org_member(((storage.foldername(name))[2])::uuid)
      )
  $policy$, v_bucket);

  -- No INSERT, UPDATE, or DELETE policy for client roles. In PostgreSQL an
  -- RLS-enabled table with no permissive policy for an operation denies that
  -- operation outright, so these three are closed by omission. They are named
  -- here only so a future reader sees the omission is deliberate.

  raise notice 'Storage bucket % configured as private with organization-scoped read policy.', v_bucket;
end;
$storage$;

-- -----------------------------------------------------------------------------
-- Guard against the single worst configuration mistake in this system:
-- flipping the lease bucket to public. Re-run as a post-deploy check.
-- -----------------------------------------------------------------------------
create or replace function app.assert_lease_bucket_is_private()
returns void
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_public boolean;
begin
  if not exists (
    select 1 from information_schema.tables
    where table_schema = 'storage' and table_name = 'buckets'
  ) then
    return;
  end if;

  execute 'select public from storage.buckets where id = $1'
    into v_public using 'lease-documents';

  if v_public is null then
    raise exception 'The lease-documents bucket does not exist'
      using errcode = 'no_data_found';
  end if;

  if v_public then
    raise exception
      'The lease-documents bucket is PUBLIC. Lease documents must never be publicly readable.'
      using errcode = 'insufficient_privilege';
  end if;
end;
$fn$;

comment on function app.assert_lease_bucket_is_private() is
  'Post-deploy check: raises if the lease document bucket has been made public.';

select app.assert_lease_bucket_is_private();
