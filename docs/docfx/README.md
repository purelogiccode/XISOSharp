# DocFX API Reference

The generated API reference for the `XISOSharp` library is built from the XML
documentation and published at:

<https://purelogiccode.github.io/XISOSharp/api/>

## Local build

From the repository root:

```powershell
dotnet tool restore
dotnet docfx metadata docs/docfx/docfx.json
dotnet docfx build docs/docfx/docfx.json
```

The output is written to `docs/api/` (gitignored). The Pages workflow stages the
Docsify site in `_site/` and builds the API into `_site/api/` instead, so the
published site stays clean.

## Theme

- Docsify uses the dark theme in `docs/index.html`.
- DocFX uses the **modern** template with `defaultTheme: 'dark'` in
  `docs/docfx/template/public/main.js`.
