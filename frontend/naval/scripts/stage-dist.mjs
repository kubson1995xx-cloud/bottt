import {readdir,lstat,readFile,writeFile,mkdir,rename,rm,copyFile,realpath} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';import path from 'node:path';import {createHash,randomUUID} from 'node:crypto';
const frontend=fileURLToPath(new URL('../',import.meta.url)),dist=path.join(frontend,'dist');
const webroot=path.resolve(frontend,'../../src/Apex.Desktop/wwwroot'),target=path.join(webroot,'naval');
async function plain(p,dir=false){const s=await lstat(p);if(s.isSymbolicLink()||(dir?!s.isDirectory():!s.isFile()))throw Error('INVALID_BUILD_ARTIFACT');return s;}
await plain(dist,true);await plain(webroot,true);if(await realpath(webroot)!==webroot)throw Error('WEBROOT_SYMLINK');
const artifacts=[];
async function walk(dir,prefix=''){for(const name of await readdir(dir)){const rel=prefix+name,p=path.join(dir,name);if(rel==='build-manifest.json')continue;if(rel==='assets'){await plain(p,true);await walk(p,'assets/');continue;}if(!/^(index\.html|assets\/[\w-]+\.(js|css))$/.test(rel))throw Error('UNEXPECTED_BUILD_ARTIFACT');await plain(p);const bytes=await readFile(p);artifacts.push({path:rel,bytes:bytes.length,sha256:createHash('sha256').update(bytes).digest('hex')});}}
await walk(dist);artifacts.sort((a,b)=>a.path.localeCompare(b.path,'en'));if(!artifacts.some(a=>a.path==='index.html')||!artifacts.some(a=>a.path.endsWith('.js'))||!artifacts.some(a=>a.path.endsWith('.css')))throw Error('INCOMPLETE_BUILD');
const html=await readFile(path.join(dist,'index.html'),'utf8');if(/<style|\sstyle=|\son[a-z]+=|<script(?![^>]*src=)[^>]*>|apex-token/i.test(html))throw Error('UNSAFE_INDEX');for(const url of [...html.matchAll(/(?:src|href)="(\/naval\/[^"]+)"/g)].map(m=>m[1]))if(!artifacts.some(a=>'/naval/'+a.path===url))throw Error('MISSING_ENTRY_ARTIFACT');
const manifest=JSON.stringify({schema:'naval-build-v1',artifacts},null,2)+'\n';await writeFile(path.join(dist,'build-manifest.json'),manifest);
const nonce=randomUUID(),candidate=path.join(webroot,'.naval-stage-'+nonce),backup=path.join(webroot,'.naval-backup-'+nonce);let moved=false;
try{await mkdir(path.join(candidate,'assets'),{recursive:true});for(const a of artifacts)await copyFile(path.join(dist,a.path),path.join(candidate,a.path));await writeFile(path.join(candidate,'build-manifest.json'),manifest);
 try{await plain(target,true);await rename(target,backup);moved=true;}catch(e){if(e.code!=='ENOENT')throw e;}
 try{await rename(candidate,target);}catch(e){if(moved)await rename(backup,target);moved=false;throw e;}
 if(moved)await rm(backup,{recursive:true});console.log('Staged Naval build:',artifacts.length,'validated artifacts');
}finally{await rm(candidate,{recursive:true,force:true});}
