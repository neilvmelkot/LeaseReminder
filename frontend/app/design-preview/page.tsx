import { notFound } from "next/navigation";
import { Plus, Upload } from "lucide-react";

import {
  Avatar,
  AvatarStack,
  Button,
  EmptyState,
  Metric,
  PageHeader,
  Panel,
  Priority,
  Status,
  TabLinks,
  TableFrame,
  Td,
  Th,
  Tr,
} from "@/components/ui/primitives";
import { fixtureCards } from "./fixtures";
import { PreviewBoard, PreviewTaskTable } from "./preview-interactive";

/**
 * A design harness.
 *
 * <h3>Why this page exists</h3>
 *
 * Every real page is behind authentication and needs live data, which makes
 * the design impossible to review without a configured Supabase project and a
 * seeded database. That is a bad feedback loop for visual work: you cannot see
 * what you changed.
 *
 * This renders the whole component vocabulary against fixture data, so the
 * typography, spacing, colour, density, and the board interaction can be
 * looked at directly.
 *
 * <h3>Why it cannot reach production</h3>
 *
 * It returns a 404 outside development. A design harness is harmless, but a
 * route that renders without a session is still a route that renders without a
 * session, and the correct number of those in a production build is zero. The
 * check is on NODE_ENV, which Next sets at build time, so the page is not
 * merely hidden: there is nothing to request.
 */
export const dynamic = "force-dynamic";

export const metadata = { title: "Design preview" };

export default function DesignPreviewPage() {
  if (process.env.NODE_ENV === "production") {
    notFound();
  }

  return (
    <div className="min-h-dvh bg-page">
      <div className="mx-auto max-w-[90rem] px-6 py-8">
        <div className="mb-8 border-b border-line pb-5">
          <p className="eyebrow mb-1">Development only</p>
          <h1>Design preview</h1>
          <p className="mt-1 max-w-2xl text-[13.5px] leading-relaxed text-ink-secondary">
            Fixture data, so the type scale, spacing, colour, and the board
            interaction can be reviewed without a session or a seeded database.
            This route 404s outside development.
          </p>
        </div>

        {/* ---------------------------------------------------------------- */}
        {/* Type scale                                                       */}
        {/* ---------------------------------------------------------------- */}
        <Section title="Typography">
          <div className="space-y-3">
            <h1>Heading one, 21px, tightened tracking</h1>
            <h2>Heading two, 16px, for panel titles</h2>
            <h3>Heading three, 15px</h3>
            <p className="text-[13.5px] leading-relaxed">
              Body text at 13.5px. The root is 15px so a table row stays compact
              without the text reading as a spreadsheet. Inter is used throughout,
              with its disambiguation set enabled so 1, l and I are separable in a
              column of unit numbers.
            </p>
            <p className="text-[12.5px] text-ink-secondary">
              Secondary text at 12.5px, measured at 4.68:1 against the page
              background. That clears WCAG 2.2 AA for body text.
            </p>
            <p className="tabular text-[13.5px]">
              Tabular figures: 1,240.00 · 998.50 · 12,300.75 · 0.00
            </p>
          </div>
        </Section>

        {/* ---------------------------------------------------------------- */}
        {/* Colour                                                           */}
        {/* ---------------------------------------------------------------- */}
        <Section title="Colour, with measured contrast">
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <Swatch name="Accent" hex="#2f52c8" ratio="7.0:1 on white" className="bg-accent" />
            <Swatch name="Ink" hex="#141c2b" ratio="16.9:1" className="bg-ink" />
            <Swatch
              name="Success fill"
              hex="#16865b"
              ratio="4.57:1 — fills only"
              className="bg-success"
            />
            <Swatch
              name="Warning fill"
              hex="#b7791f"
              ratio="3.64:1 — fills only"
              className="bg-warning"
            />
            <Swatch name="Danger" hex="#c0393a" ratio="5.40:1" className="bg-danger" />
            <Swatch
              name="Line"
              hex="#e5e8ee"
              ratio="non-text"
              className="bg-line"
              darkText
            />
          </div>

          <p className="mt-3 rounded-control border border-line bg-surface-subtle px-3 py-2
                        text-[12.5px] leading-relaxed text-ink-secondary">
            The specification&apos;s amber (#B7791F) reaches 3.43:1 on the page
            background and its green (#16865B) reaches 4.30:1, both below the 4.5:1
            needed for body text. Each is therefore split: the original for fills and
            borders, and a darkened variant for anything read as text (
            <span className="font-medium text-warning-text">#85570F at 5.6:1</span>
            {" and "}
            <span className="font-medium text-success-text">#11704B at 6.0:1</span>).
          </p>
        </Section>

        {/* ---------------------------------------------------------------- */}
        {/* Controls                                                         */}
        {/* ---------------------------------------------------------------- */}
        <Section title="Buttons and status">
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="primary">
              <Upload className="size-4" aria-hidden="true" />
              Primary
            </Button>
            <Button>Secondary</Button>
            <Button variant="subtle">Subtle</Button>
            <Button variant="ghost">Ghost</Button>
            <Button variant="danger">Danger</Button>
            <Button variant="primary" disabled>
              Disabled
            </Button>
          </div>

          <div className="mt-4 flex flex-wrap items-center gap-2">
            <Status tone="neutral">Draft</Status>
            <Status tone="info">Upcoming</Status>
            <Status tone="success">Approved</Status>
            <Status tone="warning">Pending review</Status>
            <Status tone="danger">Rejected</Status>
          </div>

          <div className="mt-4 flex flex-wrap items-center gap-2">
            <Priority priority="urgent" />
            <Priority priority="high" />
            <Priority priority="medium" />
            <Priority priority="low" alwaysShow />
            <span className="text-[12px] text-ink-secondary">
              Low renders nothing on a card unless asked: forty chips saying
              &ldquo;Low&rdquo; compete with the two that say Urgent.
            </span>
          </div>

          <div className="mt-4 flex flex-wrap items-center gap-4">
            <Avatar name="Ada Owner" />
            <Avatar name="Mike Plumbing Ltd" />
            <Avatar name={null} />
            <AvatarStack names={["Ada Owner", "Mia Manager", "Mike Plumbing", "Rory Reader"]} />
          </div>
        </Section>

        {/* ---------------------------------------------------------------- */}
        {/* Metrics                                                          */}
        {/* ---------------------------------------------------------------- */}
        <Section title="Summary metrics">
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-3 xl:grid-cols-6">
            <Metric label="Active leases" value={12} href="#" />
            <Metric label="Monthly rent" value="$28,400" hint="Active leases only" href="#" />
            <Metric label="Properties" value={4} href="#" />
            <Metric label="Open tasks" value={8} href="#" />
            <Metric
              label="Pending review"
              value={2}
              hint="Not sending reminders"
              tone="warning"
              href="#"
            />
            <Metric
              label="Needs attention"
              value={1}
              hint="Reminders not going out"
              tone="danger"
              href="#"
            />
          </div>
        </Section>

        {/* ---------------------------------------------------------------- */}
        {/* The board                                                        */}
        {/* ---------------------------------------------------------------- */}
        <Section title="Maintenance board (drag a card)">
          <div className="mb-3">
            <TabLinks
              activeKey="all"
              tabs={[
                { key: "all", label: "All properties", href: "#", count: fixtureCards.length },
                { key: "maple", label: "Maple Street", href: "#", count: 4 },
                { key: "oak", label: "Oak Avenue", href: "#", count: 3 },
              ]}
            />
          </div>

          <PreviewBoard />

          <p className="mt-2 text-[12px] text-ink-secondary">
            Dragging works. The move call will fail against the fixture
            organization and the board rolls back with the reason, which is itself
            the error path worth seeing.
          </p>
        </Section>

        {/* ---------------------------------------------------------------- */}
        {/* Table and states                                                 */}
        {/* ---------------------------------------------------------------- */}
        <Section title="Table, worst first">
          <PreviewTaskTable />
        </Section>

        <Section title="Lease table">
          <Panel title="Leases" description="Three independent statuses, shown separately.">
            <TableFrame>
              <thead>
                <tr>
                  <Th>Lease</Th>
                  <Th className="text-right">Rent</Th>
                  <Th>Term</Th>
                  <Th>Approval</Th>
                  <Th>Lifecycle</Th>
                  <Th>Reminders</Th>
                </tr>
              </thead>
              <tbody>
                <Tr>
                  <Td className="font-medium">
                    Maple Street #2
                    <p className="mt-0.5 text-[12px] font-normal text-ink-secondary">
                      Tomas Nowak
                    </p>
                  </Td>
                  <Td className="tabular text-right font-medium">$2,400.00</Td>
                  <Td className="tabular text-[13px] text-ink-secondary">
                    2026-01-01 to 2026-12-31
                  </Td>
                  <Td>
                    <Status tone="success">Approved</Status>
                  </Td>
                  <Td>
                    <Status tone="success">Active</Status>
                  </Td>
                  <Td>
                    <Status tone="success">Reminders on</Status>
                  </Td>
                </Tr>
                <Tr>
                  <Td className="font-medium">
                    Oak Avenue #1
                    <p className="mt-0.5 text-[12px] font-normal text-ink-secondary">
                      Priya Raman
                    </p>
                  </Td>
                  <Td className="tabular text-right font-medium">$1,850.00</Td>
                  <Td className="tabular text-[13px] text-ink-secondary">
                    2026-03-01 to <span title="Open ended">open</span>
                  </Td>
                  <Td>
                    <Status tone="success">Approved</Status>
                  </Td>
                  <Td>
                    <Status tone="success">Active</Status>
                  </Td>
                  <Td>
                    {/* The case a single status column would hide. */}
                    <Status tone="warning">Reminders paused</Status>
                  </Td>
                </Tr>
                <Tr>
                  <Td className="font-medium">Pine Road #4</Td>
                  <Td className="tabular text-right font-medium">$3,100.00</Td>
                  <Td className="tabular text-[13px] text-ink-secondary">
                    2026-06-01 to 2027-05-31
                  </Td>
                  <Td>
                    <Status tone="warning">Pending review</Status>
                  </Td>
                  <Td>
                    <Status tone="neutral">Draft</Status>
                  </Td>
                  <Td>
                    <Status tone="neutral">No reminders set up</Status>
                  </Td>
                </Tr>
              </tbody>
            </TableFrame>
          </Panel>
        </Section>

        <Section title="Empty state">
          <Panel>
            <EmptyState
              title="No maintenance tasks yet"
              description="Log a repair, an inspection, or a turnover job and it will appear here.
                           Tasks reported by a tenant can notify them automatically when the
                           work is finished."
              action={
                <Button variant="primary">
                  <Plus className="size-4" aria-hidden="true" />
                  New task
                </Button>
              }
            />
          </Panel>
        </Section>

        <Section title="Page header">
          <div className="surface px-5 py-4">
            <PageHeader
              title="Maintenance & tasks"
              description="Every property. Moving a card between columns notifies the people a
                           rule names; reordering within one does not."
              meta={<Status tone="danger">2 overdue</Status>}
              primaryAction={
                <Button variant="primary" size="sm">
                  <Plus className="size-3.5" aria-hidden="true" />
                  New task
                </Button>
              }
            />
          </div>
        </Section>
      </div>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="mb-10">
      <h2 className="mb-3 border-b border-line pb-1.5">{title}</h2>
      {children}
    </section>
  );
}

function Swatch({
  name,
  hex,
  ratio,
  className,
  darkText = false,
}: {
  name: string;
  hex: string;
  ratio: string;
  className: string;
  darkText?: boolean;
}) {
  return (
    <div className="surface overflow-hidden">
      <div
        className={`flex h-16 items-end px-3 pb-2 ${className} ${
          darkText ? "text-ink" : "text-white"
        }`}
      >
        <span className="font-mono text-[11px] font-medium">{hex}</span>
      </div>
      <div className="px-3 py-2">
        <p className="text-[12.5px] font-medium">{name}</p>
        <p className="text-[11.5px] text-ink-secondary">{ratio}</p>
      </div>
    </div>
  );
}
