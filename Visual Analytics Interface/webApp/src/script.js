const queryParams = new URLSearchParams(window.location.search);
const edgeHost = queryParams.get("edge") || window.location.hostname || "localhost";
const edgePort = queryParams.get("port") || "8766";
const wsProtocol = window.location.protocol === "https:" ? "wss" : "ws";
const WS_URL = `${wsProtocol}://${edgeHost}:${edgePort}`;
const STORAGE_KEY = "halasRealtimeState";
const MODEL_MANIFEST_URL = "obj/realtime/latest_models.json";
let realtimeSocket = null;
let reconnectTimer = null;

const state = {
  events: [],
  selectedId: null,
  filter: "all",
  connected: false,
  viewerReady: false,
  pendingModels: [],
  models: {
    referent: null,
    context: null
  }
};

restoreStoredState();

const els = {
  connectionState: document.querySelector("#connectionState"),
  eventCountValue: document.querySelector("#eventCountValue"),
  deferralValue: document.querySelector("#deferralValue"),
  referentValue: document.querySelector("#referentValue"),
  eventList: document.querySelector("#eventList"),
  selectedRoute: document.querySelector("#selectedRoute"),
  selectedTitle: document.querySelector("#selectedTitle"),
  selectedInsight: document.querySelector("#selectedInsight"),
  durationValue: document.querySelector("#durationValue"),
  ubrDetails: document.querySelector("#ubrDetails"),
  aoiEvidence: document.querySelector("#aoiEvidence"),
  timeline: document.querySelector("#timeline"),
  timelineRange: document.querySelector("#timelineRange"),
  referentType: document.querySelector("#referentType"),
  referentObject: document.querySelector("#referentObject"),
  referentDetails: document.querySelector("#referentDetails"),
  contextType: document.querySelector("#contextType"),
  pipelineSteps: document.querySelector("#pipelineSteps")
};

function render() {
  renderMetrics();
  renderEvents();
  renderSelected();
  renderTimeline();
}

function renderMetrics() {
  const cloud = state.events.filter((event) => event.route === "cloud").length;
  const referents = state.events.filter((event) => hasReferent(event)).length;

  els.connectionState.textContent = state.connected ? "Live" : "Waiting";
  els.eventCountValue.textContent = `${state.events.length}`;
  els.deferralValue.textContent = `${Math.round((cloud / Math.max(state.events.length, 1)) * 100)}%`;
  els.referentValue.textContent = `${referents} / ${state.events.length}`;
}

function renderEvents() {
  const visible = state.events.filter((event) => {
    if (state.filter === "all") return true;
    return event.route === state.filter;
  });

  els.eventList.innerHTML = "";
  if (!visible.length) {
    const empty = document.createElement("div");
    empty.className = "event-empty";
    empty.textContent = "Waiting for real-time UBR events";
    els.eventList.appendChild(empty);
    return;
  }

  visible.forEach((event) => {
    const button = document.createElement("button");
    button.className = `event-item ${event.route}${event.id === state.selectedId ? " active" : ""}`;
    button.type = "button";
    const consensusPill = event.consensusScore == null
      ? ""
      : `<span class="mini-pill consensus-pill" style="--consensus-color:${consensusColor(event.consensusScore)}">Consensus ${formatScore(event.consensusScore)}</span>`;
    button.innerHTML = `
      <span class="event-stripe"></span>
      <span>
        <span class="event-name">
          <span>${escapeHtml(event.action)}</span>
          <span>${escapeHtml(event.time)}</span>
        </span>
        <span class="event-meta">${escapeHtml(event.user)} - ${escapeHtml(event.intent)}</span>
        <span class="event-score">
          <span class="mini-pill">${formatDuration(event.duration)}</span>
          <span class="mini-pill">${escapeHtml(lifecycleLabel(event))}</span>
          ${consensusPill}
        </span>
      </span>
    `;
    button.addEventListener("click", () => {
      state.selectedId = event.id;
      render();
    });
    els.eventList.appendChild(button);
  });
}

function renderSelected() {
  const event = getSelectedEvent();
  if (!event) {
    els.selectedRoute.textContent = "Waiting";
    els.selectedRoute.classList.remove("cloud");
    els.selectedTitle.textContent = "Waiting for UBR";
    els.selectedInsight.textContent = "No real-time UBR received yet.";
    els.durationValue.textContent = "0.0 s";
    els.ubrDetails.innerHTML = "";
    els.aoiEvidence.innerHTML = "";
    els.referentType.textContent = "-";
    els.contextType.textContent = "-";
    els.referentObject.className = "referent-object";
    els.referentDetails.innerHTML = "";
    els.pipelineSteps.innerHTML = "";
    return;
  }

  els.selectedRoute.textContent = lifecycleLabel(event);
  els.selectedRoute.classList.toggle("cloud", event.route === "cloud");
  els.selectedTitle.textContent = `${event.action} - ${event.referent}`;
  els.selectedInsight.textContent = event.insight;
  els.durationValue.textContent = formatDuration(event.duration);

  writeDefinitionList(els.ubrDetails, [
    ["User", event.user],
    ["Action", event.action],
    ["Intent", event.intent],
    ["Trigger", event.trigger],
    ["Duration", `${event.duration.toFixed(1)} s`],
    ["Lifecycle", lifecycleLabel(event)],
    ["Phase", event.phase || "Legacy UBR"],
    ["Route", event.route === "cloud" ? "Deferred to cloud" : "Resolved at edge"]
  ]);

  writeDerivedStats();

  els.referentType.textContent = event.referentType;
  els.contextType.textContent = event.contextType;
  els.referentObject.className = `referent-object ${referentClass(event)}`;

  writeDefinitionList(els.referentDetails, [
    ["Name", event.referent],
    ["Type", event.referentType],
    ["Transform", event.transform],
    ["Encoding", event.referentType === "Physical" ? "PNG snapshot" : event.referentType === "None" ? "None" : "GLB or delta"]
  ]);

  writePipeline(event);
}

function renderTimeline() {
  if (!state.events.length) {
    els.timelineRange.textContent = "00:00-00:00";
    els.timeline.innerHTML = "";
    return;
  }

  const maxEnd = state.events.reduce((max, event, index) => Math.max(max, eventStart(index) + event.duration), 1);
  els.timelineRange.textContent = `${state.events[0]?.time || "00:00"}-${state.events[state.events.length - 1]?.time || "00:00"}`;
  els.timeline.innerHTML = "";
  state.events.forEach((event, index) => {
    const block = document.createElement("span");
    const start = eventStart(index);
    block.className = `time-block ${event.route === "cloud" ? "cloud" : ""}`;
    block.style.left = `${Math.min(96, (start / maxEnd) * 100)}%`;
    block.style.width = `${Math.max(3, (event.duration / maxEnd) * 100)}%`;
    block.title = `${event.time} ${event.action}`;
    els.timeline.appendChild(block);
  });

  const selectedIndex = state.events.findIndex((event) => event.id === state.selectedId);
  const cursor = document.createElement("span");
  cursor.className = "time-cursor";
  cursor.style.left = `${Math.min(98, (eventStart(Math.max(selectedIndex, 0)) / maxEnd) * 100)}%`;
  els.timeline.appendChild(cursor);
}

function writeDefinitionList(node, rows) {
  node.innerHTML = "";
  rows.forEach(([key, value]) => {
    const dt = document.createElement("dt");
    const dd = document.createElement("dd");
    dt.textContent = key;
    dd.textContent = value || "-";
    node.append(dt, dd);
  });
}

function writeDerivedStats() {
  const total = Math.max(state.events.length, 1);
  const selected = getSelectedEvent();
  const byAction = countBy(state.events, (event) => event.action);
  const topAction = Object.entries(byAction).sort((a, b) => b[1] - a[1])[0] || ["-", 0];
  const contextCount = state.events.filter((event) => Boolean(event.context)).length;
  const referentCount = state.events.filter((event) => hasReferent(event)).length;
  const cloudCount = state.events.filter((event) => event.route === "cloud").length;
  const rows = [
    ["Selected duration", formatDuration(selected.duration)],
    ["Action type count", `${Object.keys(byAction).length}`],
    ["Most frequent action", `${topAction[0]} (${topAction[1]})`],
    ["Referent records", `${referentCount} / ${state.events.length}`],
    ["Context records", `${contextCount} / ${state.events.length}`],
    ["Cloud-routed events", `${cloudCount} / ${state.events.length}`]
  ];

  els.aoiEvidence.innerHTML = "";
  rows.forEach(([label, value]) => {
    const row = document.createElement("div");
    row.className = "stat-row";
    row.innerHTML = `
      <span>${escapeHtml(label)}</span>
      <strong>${escapeHtml(value)}</strong>
    `;
    els.aoiEvidence.appendChild(row);
  });

  if (selected.consensusScore != null) {
    const row = document.createElement("div");
    row.className = "stat-row consensus-row";
    row.innerHTML = `
      <span>Insight consensus</span>
      <strong>
        <span class="consensus-track">
          <span class="consensus-fill" style="width:${Math.round(clamp01(selected.consensusScore) * 100)}%; background:${consensusColor(selected.consensusScore)}"></span>
        </span>
        ${formatScore(selected.consensusScore)}
      </strong>
    `;
    els.aoiEvidence.appendChild(row);
  }

  if (selected.consensusSource) {
    const row = document.createElement("div");
    row.className = "stat-row";
    row.innerHTML = `
      <span>Consensus medoid</span>
      <strong>${escapeHtml(selected.consensusSource)}</strong>
    `;
    els.aoiEvidence.appendChild(row);
  }
}

function writePipeline(event) {
  const steps = [
    ["Capture", "XR input", "done"],
    ["UBR", "Log + Context + Referent", "done"],
    ["Edge", event.route === "cloud" ? "pending" : "completed", event.route === "cloud" ? "active" : "done"],
    ["Cloud", event.route === "cloud" ? "refined" : "skipped", event.route === "cloud" ? "done" : ""],
    ["Interface", "rendered", "done"]
  ];

  els.pipelineSteps.innerHTML = "";
  steps.forEach(([name, meta, status], index) => {
    const li = document.createElement("li");
    li.className = status;
    li.innerHTML = `<span class="step-dot">${index + 1}</span><span>${escapeHtml(name)}</span><small>${escapeHtml(meta)}</small>`;
    els.pipelineSteps.appendChild(li);
  });
}

function setupFilters() {
  document.querySelectorAll(".seg").forEach((button) => {
    button.addEventListener("click", () => {
      document.querySelectorAll(".seg").forEach((item) => item.classList.remove("active"));
      button.classList.add("active");
      state.filter = button.dataset.filter;
      renderEvents();
    });
  });
}

function setupRealtime() {
  if (realtimeSocket && [WebSocket.OPEN, WebSocket.CONNECTING].includes(realtimeSocket.readyState)) {
    return;
  }
  if (reconnectTimer) {
    window.clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }

  try {
    realtimeSocket = new WebSocket(WS_URL);
    realtimeSocket.addEventListener("open", () => {
      state.connected = true;
      renderMetrics();
    });
    realtimeSocket.addEventListener("close", () => {
      state.connected = false;
      state.events.forEach((event) => {
        if (event.lifecycleStatus === "active") event.lifecycleStatus = "incomplete";
      });
      persistState();
      realtimeSocket = null;
      render();
      reconnectTimer = window.setTimeout(setupRealtime, 3000);
    });
    realtimeSocket.addEventListener("message", (message) => {
      const payload = JSON.parse(message.data);
      handleRealtimeMessage(payload);
    });
  } catch (error) {
    state.connected = false;
  }
}

function handleRealtimeMessage(message) {
  if (message.type === "new_log_event" && message.event) {
    const normalized = normalizeLogEvent(message);
    const existingIndex = state.events.findIndex((event) =>
      normalized.actionId > 0 ? event.actionId === normalized.actionId : event.seq === normalized.seq);
    if (existingIndex >= 0) {
      const existing = state.events[existingIndex];
      state.events[existingIndex] = {
        ...existing,
        ...normalized,
        id: existing.id,
        time: existing.time
      };
      state.selectedId = existing.id;
    } else {
      state.events.unshift(normalized);
      state.selectedId = normalized.id;
    }
    persistState();
    render();
  }

  if (message.type === "aoi_cancel") {
    const target = state.events.find((event) => event.actionId === Number(message.action_id));
    if (target) {
      target.lifecycleStatus = "rejected";
      target.phase = "Cancel";
      target.insight = "The XR-side AoI controller rejected this candidate; the initial insight is withdrawn.";
      target.lastUpdateAt = Date.now();
      state.selectedId = target.id;
      persistState();
      render();
    }
  }

  if (message.type === "insight_update") {
    const actionId = Number(message.action_id || 0);
    const target = state.events.find((event) =>
      actionId > 0 ? event.actionId === actionId : event.seq === message.seq);
    if (target && target.lifecycleStatus !== "rejected") {
      target.insight = message.insight || target.insight;
      target.consensusScore = parseConsensusScore(message.consensus_score, target.consensusScore);
      target.consensusSource = message.consensus_source || target.consensusSource;
      target.route = "cloud";
      state.selectedId = target.id;
      persistState();
      render();
    }
  }

  if (message.type === "object_ready") {
    const target = getSelectedEvent();
    if (target && message.object_name) {
      target.referent = message.object_name;
      const url = message.glb_path || (message.filename ? `obj/reference/${message.user || "default"}/${message.filename}` : "");
      if (url) {
        target.referentModel = url;
        dispatchViewerModel("referent", url, message.object_name);
      }
      persistState();
      renderSelected();
    }
  }

  if (message.type === "new_glb") {
    const dataType = String(message.data_type || "").toLowerCase();
    const url = message.glb_path || message.path || message.url;
    const name = message.output_name || message.object_name || message.filename || "GLB fragment";
    if (url && dataType.includes("context")) {
      dispatchViewerModel("context", url, name);
    } else if (url) {
      dispatchViewerModel("referent", url, name);
    }
  }
}

function normalizeLogEvent(message) {
  const event = message.event;
  const data = Array.isArray(event.Data) ? event.Data[0] || {} : {};
  const route = message.insight_status === "pending" ? "cloud" : "edge";
  const action = event.Name || data.UserAction || data.ActionName || "XR behavior";
  const intent = event.Intent || data.UserIntent || "Real-time behavior analysis";

  const actionId = Number(message.action_id || event.actionId || 0);
  const lifecycleStatus = message.lifecycle_status || event.lifecycleStatus
    || (String(message.phase || event.phase || "").toLowerCase() === "end" ? "completed" : "active");

  return {
    id: actionId > 0 ? `action-${actionId}` : (message.seq ?? Date.now()),
    seq: message.seq,
    actionId,
    phase: message.phase || event.phase || "",
    fragmentSequence: message.fragment_seq ?? event.fragmentSequence ?? -1,
    lifecycleStatus,
    lastUpdateAt: Date.now(),
    time: compactTime(data.ActionInvokeTimestamp || message.start_time || new Date().toISOString()),
    user: event.User || data.User || "User1",
    action,
    intent,
    trigger: data.ActionTriggerSource || event.TriggerSource || "XR device",
    route,
    referent: data.ActionReferentName || data.ActionReferent || "Referent",
    referentType: data.ActionReferentType || "Virtual",
    contextType: data.ActionContextType || "Virtual",
    transform: data.ActionReferentTransform || data.ActionInvokeLocation || "-",
    context: data.ActionContextDescription || data.ActionContext || "Context fragment received from XR device.",
    insight: message.insight || "Insight generation is pending.",
    duration: parseDuration(event.Duration),
    insightStatus: message.insight_status || "completed",
    consensusScore: parseConsensusScore(message.consensus_score ?? event.consensus_score, null),
    consensusSource: message.consensus_source ?? event.consensus_source ?? null
  };
}

function lifecycleLabel(event) {
  if (event.lifecycleStatus === "rejected") return "Rejected by AoI";
  if (event.lifecycleStatus === "incomplete") return "Incomplete / timeout";
  if (event.lifecycleStatus === "completed") return "Completed";
  return "Active";
}

function getSelectedEvent() {
  return state.events.find((event) => event.id === state.selectedId) || state.events[0];
}

function referentClass(event) {
  if (event.referentType === "None") return "marker";
  if (/memo/i.test(event.referent)) return "memo";
  if (/marker|physical/i.test(event.referent)) return "marker";
  return "cube";
}

function eventStart(index) {
  return state.events.slice(0, index).reduce((sum, event) => sum + event.duration + 2, 0);
}

function parseDuration(value) {
  if (typeof value === "number") return value;
  if (!value) return 2.5;
  const match = String(value).match(/(\d+(?:\.\d+)?)/);
  return match ? Math.max(0.5, Number(match[1])) : 2.5;
}

function compactTime(value) {
  const text = String(value);
  const match = text.match(/(\d{1,2}:\d{2}(?::\d{2})?)/);
  if (match) return match[1].replace(/^00:/, "");
  return new Date().toLocaleTimeString([], { minute: "2-digit", second: "2-digit" });
}

function hasReferent(event) {
  const value = `${event.referent || ""}`.trim().toLowerCase();
  return value !== "" && value !== "none" && event.referentType !== "None";
}

function restoreStoredState() {
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY);
    if (!raw) return;
    const saved = JSON.parse(raw);
    if (!Array.isArray(saved.events)) return;

    state.events = saved.events.slice(0, 200);
    state.selectedId = state.events.some((event) => event.id === saved.selectedId)
      ? saved.selectedId
      : state.events[0]?.id || null;
    if (saved.models && typeof saved.models === "object") {
      state.models.referent = saved.models.referent || null;
      state.models.context = saved.models.context || null;
    }
  } catch (error) {
    window.sessionStorage.removeItem(STORAGE_KEY);
  }
}

function persistState() {
  try {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify({
      events: state.events.slice(0, 200),
      selectedId: state.selectedId,
      models: state.models
    }));
  } catch (error) {
    // Ignore storage quota or private-mode errors; realtime rendering still works.
  }
}

function countBy(items, getter) {
  return items.reduce((result, item) => {
    const key = getter(item) || "-";
    result[key] = (result[key] || 0) + 1;
    return result;
  }, {});
}

function formatDuration(value) {
  const duration = Number(value || 0);
  return `${duration.toFixed(1)} s`;
}

function formatScore(value) {
  const score = Number(value);
  if (!Number.isFinite(score)) return "-";
  return score.toFixed(2);
}

function parseConsensusScore(value, fallback = null) {
  if (value === null || value === undefined || value === "") return fallback;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function clamp01(value) {
  return Math.min(1, Math.max(0, Number(value) || 0));
}

function consensusColor(value) {
  const score = clamp01(value);
  if (score < 0.55) return "var(--amber)";
  if (score < 0.75) return "var(--cyan)";
  return "var(--green)";
}

function dispatchViewerModel(kind, url, name) {
  if (!url) return;
  const normalized = { url, name: name || "GLB fragment" };
  if (kind === "context") {
    state.models.context = normalized;
  } else {
    state.models.referent = normalized;
  }
  persistState();

  if (window.HALASThreeViews) {
    if (kind === "context") {
      window.HALASThreeViews.setContext(url, normalized.name);
    } else {
      window.HALASThreeViews.setReferent(url, normalized.name);
    }
    console.info("[HALAS interface] Sent model to viewer:", kind, url);
    return;
  }

  queuePendingModel(kind, normalized.url, normalized.name);
}

async function loadInitialModelManifest() {
  try {
    const response = await fetch(`${MODEL_MANIFEST_URL}?t=${Date.now()}`, { cache: "no-store" });
    if (!response.ok) return;
    const manifest = await response.json();
    loadManifestModel("context", manifest.context);
    loadManifestModel("referent", manifest.referent);
  } catch (error) {
    console.info("[HALAS interface] Model manifest unavailable");
  }
}

function loadManifestModel(kind, model) {
  if (!model?.url) return;
  dispatchViewerModel(kind, model.url, model.name || `${kind} GLB`);
}

window.addEventListener("halas-viewers-ready", (event) => {
  state.viewerReady = true;
  flushPendingModels();
  replayStoredModels();
});

function whenViewersReady(callback) {
  if (window.HALASThreeViews?.ready || state.viewerReady) {
    callback();
    return;
  }
  let completed = false;
  const run = () => {
    if (completed) return;
    completed = true;
    callback();
  };
  window.addEventListener("halas-viewers-ready", run, { once: true });
  window.setTimeout(() => {
    if (!window.HALASThreeViews?.ready) {
      setViewerError("Context viewer is not ready. Check Three.js module loading in the browser console.");
    }
    run();
  }, 3000);
}

function setViewerError(message) {
  document.querySelectorAll(".three-viewer .viewer-placeholder").forEach((node) => {
    node.textContent = message;
    node.style.display = "block";
  });
  console.warn("[HALAS interface]", message, "status=", window.HALASThreeStatus || "not loaded");
}

function queuePendingModel(kind, url, name) {
  // Model notifications may arrive before the Three.js viewers are ready.
  const existingIndex = state.pendingModels.findIndex((model) => model.kind === kind);
  const model = { kind, url, name };
  if (existingIndex >= 0) {
    state.pendingModels[existingIndex] = model;
  } else {
    state.pendingModels.push(model);
  }
  console.info("[HALAS interface] Viewer not ready, queued model:", kind, url);
}

function flushPendingModels() {
  const pending = state.pendingModels.splice(0);
  pending.forEach((model) => dispatchViewerModel(model.kind, model.url, model.name));
}

function replayStoredModels() {
  if (state.models.context) {
    dispatchViewerModel("context", state.models.context.url, state.models.context.name);
  }
  if (state.models.referent) {
    dispatchViewerModel("referent", state.models.referent.url, state.models.referent.name);
  }
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, (char) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#39;"
  }[char]));
}

setupFilters();
render();
whenViewersReady(() => {
  replayStoredModels();
  loadInitialModelManifest();
  setupRealtime();
});
