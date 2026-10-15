import {build} from 'esbuild';
import fs from 'node:fs';
import crypto from 'node:crypto';
await build({entryPoints:['components-entry.mjs'],bundle:true,minify:true,format:'esm',outfile:'vendor/material.js',legalComments:'external',target:'es2022'});
fs.copyFileSync('node_modules/@material/web/LICENSE','vendor/material-LICENSE.txt');
fs.writeFileSync('vendor/manifest.json',JSON.stringify({package:'@material/web',version:'2.5.0',source:'https://github.com/material-components/material-web',sha256:crypto.createHash('sha256').update(fs.readFileSync('vendor/material.js')).digest('hex'),bundler:'esbuild 0.28.2'},null,2));
