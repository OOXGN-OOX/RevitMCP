import net from 'node:net';
import { randomUUID } from 'node:crypto';
import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';

const HOST = '127.0.0.1';
const TIMEOUT_MS = 310_000;
const MAX_RESPONSE_BYTES = 8 * 1024 * 1024;


// The factory allows isolated TCP tests; MCP callers cannot override the endpoint.
export function createRevitClient({ port, timeoutMs = TIMEOUT_MS, tokenLoader = async () => { throw new Error("Instance token is required."); },
  maxResponseBytes = MAX_RESPONSE_BYTES, maxRequestBytes = 1024 * 1024 } = {}) {
 return async function call(tool, args = {}, { signal } = {}) {
  signal?.throwIfAborted();
  const token = await tokenLoader();
  signal?.throwIfAborted();
  if (typeof token !== 'string' || token.length < 32) throw new Error('Invalid bridge token. Restart the Revit add-in.');
  const id = randomUUID();
  const payload = `${JSON.stringify({ id, token, tool, arguments: args })}\n`;
  if (Buffer.byteLength(payload, 'utf8') > maxRequestBytes) throw new Error('Revit request exceeds the 1 MB limit. Reduce the batch.');

  return await new Promise((resolve, reject) => {
    const socket = net.createConnection({ host: HOST, port });
    let data = '';
    let settled = false;
    let bytes = 0;
    const abort = () => finish(new Error('MCP client cancelled the Revit request.'));
    const finish = (error, value) => {
      if (settled) return;
      settled = true;
      clearTimeout(deadline);
      signal?.removeEventListener('abort', abort);
      socket.destroy();
      error ? reject(error) : resolve(value);
    };

    const deadline = setTimeout(() => finish(new Error('Timed out waiting for Revit. Check the Revit confirmation dialog.')), timeoutMs);
    socket.setEncoding('utf8');
    signal?.addEventListener('abort', abort, { once: true });
    if (signal?.aborted) { abort(); return; }
    socket.on('connect', () => socket.write(payload));
    socket.on('data', chunk => {
      bytes += Buffer.byteLength(chunk, 'utf8');
      if (bytes > maxResponseBytes) {
        finish(new Error('Revit response exceeded the 8 MB limit. Narrow the request and retry.'));
        return;
      }
      data += chunk;
      const newline = data.indexOf('\n');
      if (newline < 0) return;
      try {
        const response = JSON.parse(data.slice(0, newline));
        if (!response || response.id !== id) throw new Error('Response request ID does not match.');
        if (typeof response.ok !== 'boolean') throw new Error('Response has no boolean ok field.');
        if (!response.ok) {
          const error=new Error(response.error ?? 'Revit returned an unspecified error.');
          error.details=response.errorDetails;
          finish(error);
        }
        else finish(null, response.result);
      } catch (error) {
        finish(new Error(`Invalid response from Revit: ${error.message}`));
      }
    });
    socket.on('error', error => finish(new Error(`Cannot connect to Revit at ${HOST}:${port}. Open Revit and confirm the Revit MCP add-in loaded. ${error.message}`)));
    socket.on('end', () => {
      if (!settled) finish(new Error('Revit closed the bridge connection before returning a response.'));
    });
    socket.on('close', () => finish(new Error('Revit bridge connection closed.')));
  });
 };
}

export async function discoverInstances(directory) {
  if (directory === undefined) {
    if (!process.env.LOCALAPPDATA) throw new Error('LOCALAPPDATA is required for Revit instance discovery.');
    directory = path.join(process.env.LOCALAPPDATA, 'RevitMCP', 'instances');
  }
  let names;
  try { names = await readdir(directory); } catch (error) { if (error.code === 'ENOENT') return []; throw error; }
  const rows = await Promise.all(names.filter(name => /^\d+\.json$/.test(name)).map(async name => {
    try {
      const descriptor = JSON.parse(await readFile(path.join(directory, name), 'utf8'));
      const { processId, instanceId, port, token } = descriptor;
      if (!Number.isSafeInteger(processId) || processId <= 0 || `${processId}.json` !== name
        || typeof instanceId !== 'string' || !/^[a-f0-9]{32}$/i.test(instanceId)
        || !Number.isInteger(port) || port < 1 || port > 65535 || typeof token !== 'string' || token.length < 32) return null;
      const info = await createRevitClient({ port, tokenLoader: async () => token, timeoutMs: 1500 })('connection_info');
      if (info.processId !== processId || info.instanceId !== instanceId) return null;
      return { processId, instanceId, port, token, documentName: info.documentName };
    } catch { return null; }
  }));
  return rows.filter(Boolean).sort((a,b) => a.processId-b.processId);
}

export async function getRunningInstances() {
  return { instances: (await discoverInstances()).map(({ token, ...info }) => info) };
}

export function createDiscoveredClient(discover = discoverInstances) {
 return async function(tool, args = {}, options = {}) {
  options.signal?.throwIfAborted();
  const { processId, ...argumentsWithoutRouting } = args;
  const instances = await discover();
  const instance = processId === undefined
    ? (instances.length === 1 ? instances[0] : null)
    : instances.find(row => row.processId === processId);
  if (!instance) throw new Error(instances.length > 1 && processId === undefined
    ? 'Several Revit instances are running. Use get_running_revit_instances and pass processId.'
    : 'Requested Revit instance is not available. Start Revit with the current loader and call get_running_revit_instances.');
  return createRevitClient({ port: instance.port, tokenLoader: async () => instance.token })(tool, argumentsWithoutRouting, options);
 };
}
export const callRevit = createDiscoveredClient();
