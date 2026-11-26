// Every request carries the custom header the server requires for changes; the browser adds Origin itself.
export class ApiError extends Error {
  constructor(message, status, problems) { super(message); this.status = status; this.problems = problems || [message]; }
}

export async function api(method, path, body, signal) {
  const options = { method, headers: { 'X-Autopilot-Request': '1' }, credentials: 'same-origin', signal };
  if (body !== undefined) { options.headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(body); }
  let response;
  try { response = await fetch('/api' + path, options); }
  catch (e) {
    if (e.name === 'AbortError') throw e;
    throw new ApiError('Cannot reach Dev Autopilot. Is "autopilot ui" still running?', 0);
  }
  const text = await response.text();
  const data = text ? safeJson(text) : null;
  if (!response.ok) throw new ApiError((data && data.error) || `Request failed (${response.status}).`, response.status, data && data.problems);
  return data;
}

function safeJson(text) { try { return JSON.parse(text); } catch { return null; } }

export const get = (path, signal) => api('GET', path, undefined, signal);
export const post = (path, body) => api('POST', path, body ?? {});
export const put = (path, body) => api('PUT', path, body);
export const del = path => api('DELETE', path);
