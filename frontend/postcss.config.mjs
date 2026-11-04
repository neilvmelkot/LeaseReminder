// Tailwind v4 is configured in CSS, not JavaScript. The design tokens live in
// an @theme block in app/globals.css; see that file for the palette and the
// contrast measurements behind it.
const config = {
  plugins: {
    "@tailwindcss/postcss": {},
  },
};

export default config;
