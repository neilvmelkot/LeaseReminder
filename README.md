# LeaseReminder

Lease and rent reminder management: upload a lease, review what was extracted
from it, approve it, and have rent and lease-date reminders sent from your own
Gmail account.
---

## Why it is shaped this way

Three decisions account for most of the design, and they are worth stating
before the file listing.

**The database is the security boundary, not the API.** Every table is behind
PostgreSQL Row Level Security, and the API connects as a role that is not a
table owner and has no `BYPASSRLS`. So a forged organization id in a request
does not grant access, it selects zero rows; and a query somebody adds in six
months, without reading any of this, is filtered too. The API's own
authorization checks are still there, but they exist to turn a silent empty
result into an explicit 403, not to be the thing standing between one
landlord's leases and another's.

**A lease is never activated by software.** Extraction proposes; a person
approves. That is enforced three times over: the API contract has no approval
fields, approval is a distinct endpoint that records who and when, and a
`CHECK` constraint makes an unapproved-but-active lease unrepresentable. The
last one is the one that matters, because it holds even against a direct SQL
mistake.

**Dates are not timestamps.** A rent due date is a calendar date in the
property's time zone. Treating it as an instant is how an application tells a
tenant in Los Angeles that rent is due on the 31st when the lease says the 1st.
Due dates are `LocalDate` throughout, converted to a UTC send instant only at
the point a reminder is scheduled, and that conversion handles the two local
times a year that either do not exist or happen twice.

---

## Repository layout

```
db/
  migrations/           23 reviewed SQL migrations: schema, RLS, roles, grants
  tests/                the security acceptance tests from the spec, as SQL,
                        plus the maintenance and document-pipeline suites
  migrate.sh            applies migrations in order, with a checksum ledger
  test.sh               runs the SQL suites
  reset.sh              rebuilds the local database from scratch

backend/
  src/LeaseReminder.Domain/          entities and enums; depends on nothing
  src/LeaseReminder.Application/     the pure engines: rent schedules, send
                                     instants, idempotency keys, templates
  src/LeaseReminder.Infrastructure/  EF Core, the RLS interceptor, AES-GCM
  src/LeaseReminder.Api/             ASP.NET Core minimal API
  src/LeaseReminder.Worker/          background dispatcher
  tests/                             316 unit tests, 17 integration tests
  build.sh                           builds and tests in the .NET SDK container

frontend/
  app/                  Next.js App Router pages
  components/           the shared component vocabulary
  lib/                  Supabase clients, API client, formatting
  build.sh              type-checks, lints, and builds in a Node container

docs/
  setup.md              every account, key, and setting, in order. START HERE
```

---

## Running it locally

There is no .NET SDK or Node installation required: both builds run in
containers, which is also how the production images are built, so a green build
locally means the same thing as a green build in CI.

```bash
# 1. The database
docker compose up -d postgres
./db/migrate.sh local

# 2. The application database roles need passwords.
#    The migrations create them NOLOGIN with none, on purpose: no credential
#    belongs in source control.
docker exec -i leasereminder-postgres psql -U postgres -d leasereminder -c \
  "alter role app_api    with login password 'localdev';
   alter role app_worker with login password 'localdev';"

# 3. Verify the isolation guarantees
./db/test.sh local          # 200 assertions across 3 suites
./backend/build.sh test     # 316 unit + 17 integration tests

# 4. Run it
cp .env.example .env        # then fill in the Supabase values
./backend/build.sh build
./frontend/build.sh install
```

`./db/reset.sh` rebuilds the database from scratch. It drops the application
roles so the grants are recreated cleanly, which means **step 2 has to be
repeated after every reset**. If the integration tests report 17 *skipped*
rather than 17 passed, that is the reason.

### Looking at the interface

Every application page sits behind a session and needs seeded data, which
makes the design awkward to review and easy to regress without noticing. So
there is a harness:

```bash
cd frontend && npm run dev
# then open http://localhost:3000/design-preview
```

It renders the type scale, the measured colour table, every control, the board
with fixture cards, and the table and empty states, against fixture data and
no session. It returns 404 outside development, and the proxy's exemption for
it is likewise development-only, so there are two independent reasons it
cannot be reached in production.

It is worth running after any change to `components/ui/primitives.tsx` or
`app/globals.css`. Three bugs found this way — a hydration mismatch that
silently broke the board's screen-reader instructions, completed tasks reading
"Due in -10 days" in urgency amber, and raw enum values in a status column —
were all invisible from the type checker and the test suite.

### A note for anyone on Windows or macOS

`./frontend/build.sh` runs the build inside a Linux container against a
bind-mounted source tree. `node_modules` is deliberately masked by a named
Docker volume so the container's Linux install cannot overwrite the host's.
Without that masking, a single `./frontend/build.sh install` leaves the host
unable to run `npm run dev` at all: the native Tailwind binary is wrong for
the platform, and on Windows the `.cmd` shims disappear, so `next` and
`eslint` quietly resolve to whatever else is on `PATH`.

For the same reason `package.json` invokes `node ./node_modules/<tool>` rather
than bare `next` / `eslint` / `tsc`.

---

## What is built

### Phase 1, complete and tested

**Database.** Twenty-three migrations covering every table the full product needs,
not just phase 1: properties, units, tenants, leases, documents, extractions
and their per-field confidence and source pages, rent schedules, notification
rules at three levels, recipients, email accounts and templates, the durable
send queue, job queue, audit log, and rate limit counters. Row Level Security
on every user-owned table for all four operations, two least-privilege roles,
and column-level privileges that put the Gmail token out of the API's reach
entirely.

**Verified isolation.** `db/tests/rls_isolation_test.sql` is the executable
form of the ten security acceptance tests in the specification: user A cannot
read user B's lease, a tampered request cannot change another user's property,
duplicate worker execution cannot send duplicate reminders, a disconnected
Gmail account cannot continue sending, and so on. 40-plus assertions, all
passing. The .NET integration suite then re-asserts the same properties through
EF Core, because the SQL suite proves the *policies* are right while the C#
suite proves the *application* actually benefits from them.

**The scheduling engines**, with 150 unit tests:

- Rent recurrence: monthly by first, last, specific day, or first business day;
  weekly, fortnightly, every 30 days, one-time, and custom sequences. Leap
  years, the 31st in February with three configurable fallbacks, opt-in weekend
  and holiday shifting against a user-defined calendar, and per-occurrence
  overrides.
- Send instants: local date plus offset plus local time plus IANA zone to a
  single UTC instant, with explicit handling of the spring-forward gap and the
  autumn overlap.
- Idempotency keys: deterministic, culture-independent, and sensitive to
  exactly the fields that change *when* a reminder is sent and no others.
- Rule inheritance: global, property, and lease levels with override and
  disable, including the guarantee that editing one lease's rules cannot alter
  another's.
- Template rendering: HTML-escaped, single-pass so a substituted value is never
  re-scanned, and allowlisted so an unknown placeholder is dropped rather than
  echoed.

**The API**: Supabase JWT authentication with asymmetric keys and pinned
algorithms, authentication required by default rather than per endpoint, a
strict CORS allowlist, rate limits, security headers, request size caps, and
`ProblemDetails` errors that carry a correlation id and nothing else.
Properties, units, tenants, and leases, plus the approval, rejection, and
automation pause and resume endpoints.

**The worker**: a durable dispatch loop built on `FOR UPDATE SKIP LOCKED`,
holding no schedule in memory, re-checking every send precondition at claim
time, sending outside all database locks, and recording the result and the
email log in one transaction. It has no table privileges at all.

**The frontend**: the design system from the specification with the contrast
problems in it fixed (see below), a responsive shell, and the dashboard,
property, tenant, and lease pages.

**Deployment**: multi-stage Docker builds producing non-root images with no SDK
and no source, a Render blueprint with no secret values in it, and a CI
pipeline that applies the migrations to a clean database and runs both
isolation suites against it.

### Phase 2, complete and tested

Document intelligence. A lease is uploaded, read, and presented for a human to
check before anything acts on it.

| Step | Where |
| --- | --- |
| Signature validation, by content not filename | `Application/Documents/FileSignatureInspector.cs` |
| Server-generated storage paths | `Application/Documents/DocumentStoragePath.cs` |
| Private bucket, short-lived signed URLs | `Infrastructure/Storage/SupabaseDocumentStorage.cs` |
| PDF and Word text recovery | `Infrastructure/Documents/DocumentTextExtractor.cs` |
| Structured extraction (Gemini Flash) | `Infrastructure/Documents/GeminiLeaseExtractor.cs` |
| Validation of what comes back | `Application/Documents/ExtractionResultParser.cs` |
| Atomic result write, approval control | `db/migrations/0019_document_pipeline.sql` |
| Two-column review screen | `frontend/app/documents/[documentId]/` |

Three notes on how this differs from the plan in section 10.

**There is no OCR engine.** The specification lists OCR as a step, and an
earlier design used Tesseract. It turned out to be unnecessary: Gemini reads
PDFs and photographs natively, so a scanned lease is the same API call as a
text one with a different part attached. That removed a native dependency, a
language-data download, and a second parser for untrusted input. The capability
is present; the component is not, because it would have been redundant.

**Every value is typed as a string in the response schema.** Declaring dates as
dates would let the model coerce a malformed date into a valid-looking one,
invisibly. Taking everything as text means this application parses it in one
place, and a date it could not read reaches a human as "could not read this"
rather than as a confident wrong date.

**Approval is enforced by the database, not the screen.**
`app.approve_lease_from_extraction` requires a named human, refuses while any
field is undecided or any blocking issue is open, and writes to an append-only
table. The API role holds no INSERT on `lease_approvals`, and the `leases`
table's own CHECK constraint refuses to activate an unapproved lease, so there
is no second path. Deleting the entire frontend would not weaken it.

### Phase 3, the engine and the schedule editor

| Step | Where |
| --- | --- |
| Rent schedule write path | `db/migrations/0020_rent_schedule_pipeline.sql` |
| Occurrence generation worker | `Worker/RentScheduleGenerationService.cs` |
| Schedule editor with live preview | `frontend/components/rent/schedule-editor.tsx` |

The preview calls the same `RentScheduleCalculator` the worker calls, on the
definition as typed. A second implementation in the browser would be faster
and would agree right up until it did not — and the symptom of that would be a
preview that lies about when somebody gets told to pay rent.

Still missing from phase 3: the three-level reminder rule builder and recipient
group management.

### Phase 4, complete and tested

Gmail sending. A mailbox is connected once, and every reminder goes from it.

| Step | Where |
| --- | --- |
| OAuth with PKCE and state | `Infrastructure/Email/GoogleOAuthClient.cs` |
| Connect, disconnect, test send | `Api/Endpoints/EmailAccountEndpoints.cs` |
| The sender itself | `Worker/GmailEmailSender.cs` |
| Message construction | `Application/Email/MimeMessageBuilder.cs` |
| Account rules and the send log | `db/migrations/0021_gmail_connection.sql` |
| Template editing and versioning | `db/migrations/0023_email_template_editing.sql` |
| Settings, log, template editor | `frontend/app/settings/email`, `.../templates` |

Four notes.

**The API cannot read a Gmail token.** It stores one and destroys one;
`app.read_email_account_token` is granted to the worker role alone, and
migration 0010 revokes the four ciphertext columns from the API role with
column-level grants. So a flaw anywhere in the API cannot leak the credential,
because it is not reachable from that process.

**PKCE, even though this is a confidential client.** A web server with a client
secret does not strictly need it. The authorization code travels back through a
browser redirect — through history, through any proxy in between — and PKCE
makes a code captured from any of those useless. The verifier lives in an
http-only `__Host-` cookie rather than in the `state` parameter, because state
travels in a URL and the verifier must not.

**There is no fallback sender.** Section 13.B gives an organization exactly one
connected mailbox, enforced by a partial unique index. If it is disconnected or
its token stops working, nothing is sent and the failure is recorded. Nothing
is ever marked sent that was not.

**Header injection is tested, not assumed.** Every header value in a reminder
comes from a lease or a tenant record. `MimeMessageBuilderTests` tries to close
a header and open a `Bcc` from the subject, the display name, a recipient, and
the message id, and tries to break the multipart boundary from the body.

### Deferred

| Phase | What it adds |
| --- | --- |
| 3 (rest) | The three-level reminder rule builder, recipient groups |
| 5 | Monitoring, restore drills, load and failure testing, the security review |

---

## Two findings worth flagging

**The specification's amber and green do not meet WCAG 2.2 AA for body text.**
Section 5 supplies a palette and asks that contrast be verified before
finalising it. It was, and two tokens fail against the off-white page
background: `#B7791F` reaches 3.43:1 and `#16865B` reaches 4.30:1, where body
text needs 4.5:1. Rather than change the brand colours, each is split in two: a
fill variant (the original, used for borders, icons, and chart marks, where 3:1
is the relevant threshold and both pass) and a darkened text variant
(`#8A5A10` at 5.57:1 and `#157A53` at 5.02:1). White on amber is 3.64:1 and is
therefore never used; amber chips use a light tint with dark text. The
measurements are in `frontend/app/globals.css`.

**Section 2 recommends .NET 8 while noting its lifecycle should be checked.**
It was: .NET 8 reaches end of support in November 2026. The project targets
.NET 10, the current LTS.

Both of these, and the rest of the decisions taken along the way, are recorded
in the file each one governs rather than in a separate register: the migration,
the engine class, or the component carries the reasoning in its own comments,
where it is least likely to drift away from the code it explains.

---

## Documentation

**Start here:** [`docs/setup.md`](docs/setup.md) is the complete setup guide —
every account to create, every value to copy, where each one goes, and how to
tell it worked. It covers Supabase (database, storage, auth), Google AI Studio
for document extraction, Google Cloud for Gmail, local development, and the
Render deploy, with a troubleshooting section at the end.

| | |
| --- | --- |
| [`docs/setup.md`](docs/setup.md) | every account, key, and setting, in the order you need them |
| [`db/migrations/`](db/migrations/) | the schema itself, commented with the reasoning for each constraint |
| [`db/tests/`](db/tests/) | the security acceptance tests, as executable SQL |

Four further documents are phase 5 deliverables and are **not yet written**:
`architecture.md`, `security.md`, `deployment.md`, and `decisions.md`. Until
they exist, the reasoning behind each decision lives in the file it governs —
the migrations and the engine classes carry it in their comments, which is
where it is least likely to go stale.
