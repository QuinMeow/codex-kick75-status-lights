(() => {
  "use strict";

  const token = document.querySelector('meta[name="agentkick75-write-token"]').content;
  const notice = document.getElementById("notice");
  const stateBanner = document.getElementById("state-banner");
  const ledRail = document.getElementById("led-rail");
  const pauseButton = document.getElementById("pause-button");
  const saveButton = document.getElementById("save-settings");
  const hardwareTestButton = document.getElementById("hardware-test-button");
  const baselineRecovery = document.getElementById("baseline-recovery");
  const baselineRecoveryConfirmation = document.getElementById("baseline-recovery-confirmation");
  const baselineRecoveryButton = document.getElementById("baseline-recovery-button");
  let currentStatus = null;
  let currentBaselineRecovery = null;
  let noticeTimer = null;
  let statusRefreshTimer = null;

  const stateLabels = {
    idle: "Idle",
    thinking: "Thinking",
    requiresinput: "Requires input",
    "requires-input": "Requires input",
    complete: "Complete"
  };

  function setText(id, value) {
    document.getElementById(id).textContent = value ?? "—";
  }

  function canonicalState(value) {
    return String(value || "idle").replace(/[\s_-]/g, "").toLowerCase();
  }

  function displayState(value) {
    return stateLabels[canonicalState(value)] || "Idle";
  }

  function formatTime(value) {
    if (!value) {
      return "—";
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
      return "—";
    }

    return new Intl.DateTimeFormat(undefined, {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit"
    }).format(date);
  }

  async function api(path, options = {}) {
    const request = {
      method: options.method || "GET",
      credentials: "same-origin",
      headers: {
        Accept: "application/json"
      }
    };

    if (options.body !== undefined) {
      request.headers["Content-Type"] = "application/json";
      request.headers["X-AgentKick75-Token"] = token;
      request.body = JSON.stringify(options.body);
    }

    const response = await fetch(path, request);
    if (!response.ok) {
      let message = `Request failed (${response.status})`;
      try {
        const problem = await response.json();
        const firstError = problem.errors && Object.values(problem.errors).flat()[0];
        message = firstError || problem.title || message;
      } catch {
        // Keep the status-only message for non-JSON failures.
      }
      throw new Error(message);
    }

    if (response.status === 204) {
      return null;
    }

    return response.json();
  }

  function renderStatus(status) {
    currentStatus = status;
    const stateKey = status.isPaused ? "paused" : canonicalState(status.aggregateState);
    const stateLabel = status.isPaused ? "Paused" : displayState(status.aggregateState);

    const stateClass = stateKey === "requiresinput" ? "requires-input" : stateKey;
    stateBanner.className = `state-display state-${stateClass}`;
    ledRail.className = `led-rail state-${stateClass}`;
    setText("aggregate-state", stateLabel);
    setText("active-sessions", String(status.activeSessionCount ?? 0));
    setText("last-event", formatTime(status.lastEventAt));
    setText("host-mode", status.isPaused
      ? "Original lighting is restored while control is paused"
      : status.isPreviewActive
        ? "A three-second preview is active"
        : "Lighting control is active");
    setText("hook-status", status.hookStatus || "Unknown");

    const device = status.device || {};
    setText("device-model", device.model);
    setText("device-transport", device.transport);
    setText("transport-detail", device.transport);
    setText("receiver-status", device.receiverStatus);
    setText("keyboard-status", device.keyboardStatus);
    setText("keyboard-detail", device.keyboardStatus);
    setText("firmware-version", device.firmwareVersion);
    setText("device-identity", device.deviceIdentity);
    setText("interface-fingerprint", device.interfaceFingerprint || "Unknown");
    setText("support-status", device.supportStatus || "Unknown");

    const error = document.getElementById("device-error");
    error.hidden = !device.lastErrorCode;
    error.textContent = device.lastErrorCode ? `Diagnostic code: ${device.lastErrorCode}` : "";

    renderBaselineRecovery(status.baselineRecovery);

    pauseButton.querySelector(".action-title").textContent = status.isPaused ? "Resume" : "Pause";
    pauseButton.querySelector(".action-description").textContent = status.isPaused
      ? "Resume state-driven side lighting"
      : "Restore lighting and stop updates";
  }

  function renderBaselineRecovery(risk) {
    const isCurrentMismatch = risk
      && risk.code === "DeviceIdentityMismatch"
      && /^[0-9a-f]{32}$/i.test(String(risk.confirmationId || ""));
    const previousConfirmation = currentBaselineRecovery && currentBaselineRecovery.confirmationId;
    currentBaselineRecovery = isCurrentMismatch ? risk : null;
    baselineRecovery.hidden = !isCurrentMismatch;
    if (!isCurrentMismatch) {
      baselineRecoveryConfirmation.checked = false;
      baselineRecoveryButton.disabled = true;
      return;
    }

    document.getElementById("baseline-recovery-message").textContent = risk.message;
    setText("baseline-device-identity", risk.baselineDeviceIdentity || "Hidden device instance");
    setText("observed-device-identity", risk.observedDeviceIdentity || "Hidden device instance");
    if (previousConfirmation !== risk.confirmationId) {
      baselineRecoveryConfirmation.checked = false;
    }
    syncBaselineRecoveryGate();
  }

  function setStyle(prefix, style) {
    const color = String(style.color || "#000000").toUpperCase();
    const brightness = Number(style.brightness ?? 0);
    document.getElementById(`${prefix}-color`).value = color;
    document.getElementById(`${prefix}-hex`).value = color;
    document.getElementById(`${prefix}-brightness`).value = brightness;
    document.getElementById(`${prefix}-brightness-value`).textContent = `${brightness}%`;
  }

  function renderSettings(settings) {
    setStyle("thinking", settings.thinking);
    setStyle("requires-input", settings.requiresInput);
    setStyle("complete", settings.complete);
    document.getElementById("complete-hold-seconds").value = settings.completeHoldSeconds;
    document.getElementById("launch-at-sign-in").checked = Boolean(settings.launchAtSignIn);
  }

  function readStyle(prefix) {
    return {
      color: document.getElementById(`${prefix}-hex`).value.trim().toUpperCase(),
      brightness: Number(document.getElementById(`${prefix}-brightness`).value)
    };
  }

  function readSettings() {
    return {
      thinking: readStyle("thinking"),
      requiresInput: readStyle("requires-input"),
      complete: readStyle("complete"),
      completeHoldSeconds: Number(document.getElementById("complete-hold-seconds").value),
      launchAtSignIn: document.getElementById("launch-at-sign-in").checked
    };
  }

  function showNotice(message, isError = false) {
    window.clearTimeout(noticeTimer);
    notice.textContent = message;
    notice.classList.toggle("is-error", isError);
    notice.hidden = false;
    noticeTimer = window.setTimeout(() => {
      notice.hidden = true;
    }, 5000);
  }

  async function refreshStatus() {
    const status = await api("/api/v1/status");
    renderStatus(status);
  }

  function scheduleStatusRefresh() {
    window.clearTimeout(statusRefreshTimer);
    statusRefreshTimer = window.setTimeout(() => {
      refreshStatus().catch(() => {});
    }, 120);
  }

  function appendEvent(event) {
    const list = document.getElementById("event-log");
    const item = document.createElement("li");
    const time = document.createElement("time");
    const kind = document.createElement("span");
    const code = document.createElement("span");
    const summary = document.createElement("span");
    const safeKind = safeDiagnosticToken(event.kind, "unknown");
    const safeCode = safeDiagnosticToken(event.diagnosticCode, "—");

    time.dateTime = event.occurredAt || new Date().toISOString();
    time.textContent = formatTime(time.dateTime);
    kind.className = "event-kind";
    kind.textContent = safeKind;
    code.className = "event-code";
    code.textContent = safeCode;
    summary.className = "event-summary";
    summary.textContent = summarizeEventStatus(event.status);

    item.append(time, kind, code, summary);
    list.prepend(item);
    while (list.children.length > 64) {
      list.lastElementChild.remove();
    }
    setText("diagnostic-count", String(list.children.length));
  }

  function renderPersistedDiagnostics(entries) {
    const list = document.getElementById("persisted-event-log");
    list.replaceChildren();

    for (const entry of entries) {
      const item = document.createElement("li");
      const time = document.createElement("time");
      const kind = document.createElement("span");
      const code = document.createElement("span");
      const summary = document.createElement("span");

      time.dateTime = entry.timestamp || "";
      time.textContent = formatTime(entry.timestamp);
      kind.className = "event-kind";
      kind.textContent = safeDiagnosticToken(entry.eventType, "unknown");
      code.className = "event-code";
      code.textContent = safeDiagnosticToken(entry.code, "—");
      summary.className = "event-summary";
      summary.textContent = summarizePersistedDiagnostic(entry);

      item.append(time, kind, code, summary);
      list.append(item);
    }

    setText("persisted-diagnostic-count", String(list.children.length));
    setText("persisted-diagnostics-status", entries.length === 0
      ? "No saved diagnostic entries are available."
      : `Loaded ${entries.length} saved diagnostic entries.`);
  }

  function summarizePersistedDiagnostic(entry) {
    const parts = [];
    const visualState = safeDiagnosticToken(entry.visualState, "");
    const transportFailure = safeDiagnosticToken(entry.transportFailure, "");
    const latency = Number(entry.latencyMilliseconds);

    if (visualState) {
      parts.push(`State ${displayState(visualState)}`);
    }
    if (transportFailure) {
      parts.push(`Transport ${transportFailure}`);
    }
    if (entry.latencyMilliseconds !== null
      && entry.latencyMilliseconds !== undefined
      && Number.isSafeInteger(latency)
      && latency >= 0) {
      parts.push(`${latency} ms`);
    }

    return parts.length === 0 ? "No additional fields" : parts.join(" · ");
  }

  function safeDiagnosticToken(value, fallback) {
    const tokenValue = String(value || "").trim();
    return /^[a-z0-9._:-]{1,64}$/i.test(tokenValue) ? tokenValue : fallback;
  }

  function summarizeEventStatus(status) {
    if (!status) {
      return "No status snapshot";
    }

    const parts = [
      `State ${displayState(status.aggregateState)}`,
      `${Number(status.activeSessionCount ?? 0)} active`
    ];
    if (status.isPaused) {
      parts.push("paused");
    }
    if (status.isPreviewActive) {
      parts.push("preview active");
    }
    return parts.join(" · ");
  }

  function bindStyleInputs(prefix) {
    const color = document.getElementById(`${prefix}-color`);
    const hex = document.getElementById(`${prefix}-hex`);
    const brightness = document.getElementById(`${prefix}-brightness`);
    const output = document.getElementById(`${prefix}-brightness-value`);

    color.addEventListener("input", () => {
      hex.value = color.value.toUpperCase();
    });

    hex.addEventListener("input", () => {
      const value = hex.value.trim();
      if (/^#[0-9a-f]{6}$/i.test(value)) {
        color.value = value;
      }
    });

    brightness.addEventListener("input", () => {
      output.textContent = `${brightness.value}%`;
    });
  }

  async function runBusy(button, operation) {
    button.disabled = true;
    try {
      await operation();
    } catch (error) {
      showNotice(error.message || "The local command failed.", true);
    } finally {
      button.disabled = false;
    }
  }

  document.getElementById("settings-form").addEventListener("submit", event => {
    event.preventDefault();
    runBusy(saveButton, async () => {
      const settings = await api("/api/v1/settings", {
        method: "PUT",
        body: readSettings()
      });
      renderSettings(settings);
      showNotice("Settings saved.");
    });
  });

  document.getElementById("preview-button").addEventListener("click", event => {
    runBusy(event.currentTarget, async () => {
      const state = document.getElementById("preview-state").value;
      await api(`/api/v1/preview/${encodeURIComponent(state)}`, {
        method: "POST",
        body: {}
      });
      showNotice("Preview started. The previous state returns in 3 seconds.");
      scheduleStatusRefresh();
    });
  });

  pauseButton.addEventListener("click", () => {
    runBusy(pauseButton, async () => {
      const status = await api("/api/v1/pause", {
        method: "POST",
        body: { paused: !Boolean(currentStatus && currentStatus.isPaused) }
      });
      renderStatus(status);
      showNotice(status.isPaused ? "Lighting control paused." : "Lighting control resumed.");
    });
  });

  document.getElementById("restore-button").addEventListener("click", event => {
    if (!window.confirm("Restore the exact original side-light state now?")) {
      return;
    }

    runBusy(event.currentTarget, async () => {
      await api("/api/v1/restore", { method: "POST", body: {} });
      showNotice("Original lighting restored.");
      scheduleStatusRefresh();
    });
  });

  function syncBaselineRecoveryGate() {
    baselineRecoveryButton.disabled = !currentBaselineRecovery
      || !baselineRecoveryConfirmation.checked;
  }

  function resetBaselineRecoveryConfirmation() {
    baselineRecoveryConfirmation.checked = false;
    syncBaselineRecoveryGate();
  }

  baselineRecoveryConfirmation.addEventListener("change", syncBaselineRecoveryGate);
  window.addEventListener("pageshow", resetBaselineRecoveryConfirmation);
  resetBaselineRecoveryConfirmation();

  hardwareTestButton.addEventListener("click", event => {
    const selectedTransport = document.querySelector('input[name="hardware-transport"]:checked');
    runBusy(event.currentTarget, async () => {
      const result = await api("/api/v1/hardware-test", {
        method: "POST",
        body: { transport: selectedTransport.value }
      });
      showNotice(result.message || (result.succeeded ? "Hardware test completed." : "Hardware test did not pass."), !result.succeeded);
      scheduleStatusRefresh();
    });
  });

  baselineRecoveryButton.addEventListener("click", event => {
    const risk = currentBaselineRecovery;
    if (!risk || !baselineRecoveryConfirmation.checked) {
      showNotice("Confirm the device mismatch disposition first.", true);
      return;
    }

    if (!window.confirm("Abandon the old device's baseline ownership without writing its saved bytes to the currently connected device?")) {
      resetBaselineRecoveryConfirmation();
      return;
    }

    // The browser consumes this one confirmation before the request. The Host
    // independently binds it to the still-current mismatch and owned journal.
    currentBaselineRecovery = null;
    resetBaselineRecoveryConfirmation();
    runBusy(event.currentTarget, async () => {
      const result = await api("/api/v1/baseline-recovery/abandon", {
        method: "POST",
        body: { confirmationId: risk.confirmationId, confirmed: true }
      });
      showNotice(result.message || "Old baseline ownership abandoned.");
      await refreshStatus();
    }).finally(syncBaselineRecoveryGate);
  });

  ["thinking", "requires-input", "complete"].forEach(bindStyleInputs);

  document.getElementById("clear-session-log").addEventListener("click", () => {
    document.getElementById("event-log").replaceChildren();
    setText("diagnostic-count", "0");
  });

  document.getElementById("load-recent-diagnostics").addEventListener("click", event => {
    runBusy(event.currentTarget, async () => {
      const entries = await api("/api/v1/diagnostics?limit=50");
      renderPersistedDiagnostics(Array.isArray(entries) ? entries : []);
    });
  });

  Promise.all([
    refreshStatus(),
    api("/api/v1/settings").then(renderSettings)
  ]).catch(error => {
    showNotice(error.message || "Unable to load local control state.", true);
  });

  const eventSource = new EventSource("/api/v1/events");
  eventSource.onopen = () => {
    document.getElementById("connection-label").classList.remove("is-disconnected");
    setText("connection-text", "Connected");
    setText("live-state", "Connected");
  };
  eventSource.onmessage = message => {
    try {
      const event = JSON.parse(message.data);
      appendEvent(event);
      if (event.status) {
        renderStatus(event.status);
      } else {
        scheduleStatusRefresh();
      }
    } catch {
      // Ignore malformed local events and let EventSource continue.
    }
  };
  eventSource.onerror = () => {
    document.getElementById("connection-label").classList.add("is-disconnected");
    setText("connection-text", "Reconnecting");
    setText("live-state", "Reconnecting");
  };
})();
