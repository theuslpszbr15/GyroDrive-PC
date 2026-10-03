'use strict';
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const root = __dirname;
const releases = ['GyroDrive-PC-0.1.1-beta.zip', 'GyroDrive-PC-Source-0.1.1-beta.zip', 'SHA256SUMS.txt'];
function sourceFiles(directory, prefix = '') {
  return fs.readdirSync(directory, {withFileTypes:true}).flatMap(entry => {
    const name = prefix + entry.name;
    if (/\.(exe|dll|zip|log|keystore|jks)$|(^|\/)(vendor|build|dist)\//.test(name)) throw new Error('Excluded source file');
    if (entry.isDirectory()) return sourceFiles(path.join(directory, entry.name), name + '/');
    if (!entry.isFile()) throw new Error('Unexpected source entry');
    return [{name, data:fs.readFileSync(path.join(directory, entry.name)).toString('base64')}];
  });
}
http.createServer((request, response) => {
  response.setHeader('Cache-Control', 'no-store');
  if (request.method !== 'GET') {response.writeHead(405);response.end();return;}
  try {
    const route = new URL(request.url, 'http://localhost').pathname;
    const files = route === '/source' ? sourceFiles(path.join(root, 'build/release-source')) : route === '/assets' ? releases.map(name => ({name, data:fs.readFileSync(path.join(root, 'dist', name)).toString('base64')})) : null;
    if (!files) {response.writeHead(404);response.end();return;}
    response.writeHead(200, {'Content-Type':'application/json'});
    response.end(JSON.stringify(files));
  } catch {response.writeHead(500);response.end();}
}).listen(0, '127.0.0.1', function () {console.log('Publish PC files: http://127.0.0.1:' + this.address().port);});