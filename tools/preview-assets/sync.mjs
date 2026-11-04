import { cp, mkdir, rm, writeFile, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
const here=path.dirname(fileURLToPath(import.meta.url));
const source=path.join(here,'node_modules/pdfjs-dist');
const destination=path.resolve(here,'../../src/DocumentStorage.Web/wwwroot/vendor/pdfjs');
await rm(destination,{recursive:true,force:true});await mkdir(destination,{recursive:true});
for(const file of ['build/pdf.min.mjs','build/pdf.worker.min.mjs','web/pdf_viewer.mjs','web/pdf_viewer.css','web/images','LICENSE','cmaps','standard_fonts','wasm'])await cp(path.join(source,file),path.join(destination,file),{recursive:true});
await writeFile(path.join(destination,'VERSION.txt'),'pdfjs-dist 6.5.438\nApache-2.0; see LICENSE.\nRegenerate with npm ci --ignore-scripts --omit=optional && npm run sync in tools/preview-assets.\n');

// Scope generic PDF.js UI classes so they cannot override the app sidebar.
const css = await readFile(path.join(source,'web/pdf_viewer.css'),'utf8');
await writeFile(path.join(destination,'web/pdf_viewer.css'),'/* DocumentStorage modification: scoped upstream PDF.js styles; :root becomes :scope. */\n@scope (.document-preview) {\n'+css.replaceAll(':root',':scope')+'\n}\n');
