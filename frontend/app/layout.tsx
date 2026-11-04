import type { Metadata, Viewport } from "next";
import { Inter, JetBrains_Mono } from "next/font/google";

import "./globals.css";

/**
 * Typefaces.
 *
 * next/font downloads these at build time and serves them from this origin, so
 * there is no runtime request to Google, nothing to allow in the Content
 * Security Policy, and no third party learning who loaded the page.
 *
 * `display: "swap"` shows the fallback immediately and swaps when the webfont
 * arrives. The alternative, blocking, means a blank screen on a slow
 * connection; a brief reflow is the better trade for an application people
 * sign into rather than a landing page.
 *
 * Inter for everything, JetBrains Mono only for identifiers and monetary
 * detail where character-level distinction matters.
 */
const inter = Inter({
  subsets: ["latin"],
  display: "swap",
  variable: "--font-inter",
  // Only the weights actually used. Every extra weight is another file.
  weight: ["400", "500", "600", "700"],
});

const jetbrainsMono = JetBrains_Mono({
  subsets: ["latin"],
  display: "swap",
  variable: "--font-jetbrains",
  weight: ["400", "500"],
});

export const metadata: Metadata = {
  title: {
    default: "LeaseReminder",
    template: "%s · LeaseReminder",
  },
  description: "Lease, rent reminder, and property maintenance management.",

  // No indexing. Every page behind the sign-in is lease terms, addresses, and
  // tenant names, and the sign-in page itself has nothing worth finding. A
  // robots directive is a request rather than a control, so this sits
  // alongside authentication, not instead of it.
  robots: {
    index: false,
    follow: false,
    nocache: true,
  },
};

export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,

  // No maximum-scale and no user-scalable=no. Preventing zoom is a real
  // accessibility failure, and this application shows dense tables of numbers
  // that some readers will need to enlarge.
  themeColor: "#f6f7f9",
};

export default function RootLayout({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  return (
    // lang is what lets a screen reader choose the right pronunciation rules,
    // and it is the cheapest accessibility attribute there is.
    <html
      lang="en"
      className={`${inter.variable} ${jetbrainsMono.variable}`}
      // The script below sets data-sidebar on this element before React
      // hydrates, which is the whole point of it — and React then reports the
      // attribute it did not render as a hydration mismatch. This is the case
      // the opt-out exists for. It applies to this element's own attributes
      // only, not to the tree inside it, so a genuine mismatch anywhere in the
      // application is still reported.
      suppressHydrationWarning
    >
      <head>
        {/*
          Applies the saved appearance — colour theme and sidebar width —
          before the first paint.

          A raw inline script in <head>, deliberately, and not next/script
          with beforeInteractive. That strategy guarantees the script runs
          ahead of Next's own modules, which is not the same as running ahead
          of the first paint: it is injected into the body and the browser had
          already painted the page using the prefers-color-scheme media query.
          Somebody who had chosen Light on a machine set to dark saw a dark
          flash on every single load.

          Parser-blocking in <head> is the one placement that cannot flash,
          because nothing is painted until it has run. It costs a few
          microseconds and two localStorage reads.

          Next's guidance against hand-written <head> content is about <title>
          and <meta>, which belong to the Metadata API; a script is not that.

          Wrapped in try/catch because localStorage throws outright in some
          real configurations — a browser set to block site data, for one.
          Falling back to the system theme is a fine outcome; an exception
          before the page renders is not.
        */}
        <script
          dangerouslySetInnerHTML={{
            __html:
              "try{var d=document.documentElement,t=localStorage.getItem('theme');"
              + "if(t==='dark'||t==='light')d.setAttribute('data-theme',t);"
              + "if(localStorage.getItem('sidebar')==='collapsed')"
              + "d.setAttribute('data-sidebar','collapsed');}catch(e){}",
          }}
        />
      </head>

      <body className="antialiased">{children}</body>
    </html>
  );
}
