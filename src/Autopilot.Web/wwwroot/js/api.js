// Every request carries the custom header the server requires for changes; the browser adds Origin itself.
export class ApiError extends Error {
  constructor(message, status, problems) { super(message); this.status = status; this.problems = problems || [message]; }
}

export async function api(method, path, body, signal) {
  const options = { method, headers: { 'X-Autopilot-Request': '1' }, credentials: 'same-origin', signal };
  if (body !== undefined) { options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(body); }
  let response;
  const started = performance.now();
  try { response = await fetch('/api' + path, options); }
  catch (e) {
    if (e.name === 'AbortError') throw e;
    throw new ApiError('Cannot reach Autopilot. Is "autopilot ui" still running?', 0);
  }
  const text = await response.text();
  showTiming(method, path, response, performance.now() - started);
  const data = text ? safeJson(text) : null;
  if (!response.ok) throw new ApiError((data && data.error) || `Request failed (${response.status}).`, response.status, data && data.problems);
  return data;
}

// The footer shows how long the last call took in the browser and on the server, so a slow page can be seen.
function showTiming(method, path, response, total) {
  const line = document.getElementById('timing');
  if (!line) return;
  const server = /dur=([\d.]+)/.exec(response.headers.get('Server-Timing') || '');
  line.textContent = `${method} ${path.split('?')[0]}: ${Math.round(total)} ms` + (server ? ` (server ${Math.round(Number(server[1]))} ms)` : '');
  if (total > 1000) line.dataset.slow = 'true'; else delete line.dataset.slow;
}

function safeJson(text) { try { return JSON.parse(text); } catch { return null; } }

export const get = (path, signal) => api('GET', path, undefined, signal);
export const post = (path, body) => api('POST', path, body ?? {});
export const put = (path, body) => api('PUT', path, body);
export const del = path => api('DELETE', path);
