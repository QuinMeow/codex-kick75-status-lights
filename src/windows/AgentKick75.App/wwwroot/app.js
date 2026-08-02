(() => {
  "use strict";

  const token = document.querySelector('meta[name="agentkick75-write-token"]').content;
  const notice = document.getElementById("notice");
  const stateBanner = document.getElementById("state-banner");
  const ledRail = document.getElementById("led-rail");
  const pauseButton = document.getElementById("pause-button");
  const saveButton = document.getElementById("save-settings");
  const installHooksButton = document.getElementById("install-hooks-button");
  const sessionDiagnosticsEnabled = document.getElementById("session-diagnostics-enabled");
  const savedDiagnosticsEnabled = document.getElementById("saved-diagnostics-enabled");
  let currentStatus = null;
  let noticeTimer = null;
  let statusRefreshTimer = null;

  const stateLabels = {
    idle: "空闲",
    thinking: "思考中",
    requiresinput: "等待输入",
    "requires-input": "等待输入",
    complete: "已完成",
    interrupted: "已中断"
  };

  const valueLabels = {
    Unknown: "未知",
    none: "无",
    NotApplicable: "不适用",
    Present: "已连接",
    Unavailable: "不可用",
    Disconnected: "已断开",
    Ready: "就绪",
    DeviceBusy: "设备被占用",
    SleepingOrUnresponsive: "休眠或无响应",
    InvalidResponse: "响应无效",
    DiagnosticOnly: "仅诊断",
    Unsupported: "不支持",
    Enabled: "已启用",
    Disabled: "已禁用",
    Unconfirmed: "未确认",
    "USB allowlisted; runtime session observed": "USB 已允许；已建立运行会话",
    "USB allowlisted; descriptor observed": "USB 已允许；已识别描述符"
  };

  const deviceModelLabels = {
    "Unknown HID device": "未知 HID 设备",
    "Kick75 USB HID device": "Kick75 USB HID 设备",
    "Kick75 U1 receiver": "不支持的 U1 接收器",
    "Kick75 High HID device": "不支持的 Kick75 High HID 设备"
  };

  const sessionDiagnosticStates = new Set(["thinking", "requiresinput", "complete", "interrupted"]);

  function setText(id, value) {
    document.getElementById(id).textContent = value ?? "—";
  }

  function canonicalState(value) {
    return String(value || "idle").replace(/[\s_-]/g, "").toLowerCase();
  }

  function displayState(value) {
    return stateLabels[canonicalState(value)] || "空闲";
  }

  function displayValue(value) {
    return valueLabels[value] || value || "—";
  }

  function displayDeviceModel(value) {
    return deviceModelLabels[value] || value || "未知设备";
  }

  function deviceConnectionLabel(device) {
    const keyboardStatus = String(device.keyboardStatus || "Unknown");
    if (keyboardStatus === "Ready") {
      return "已连接";
    }
    if (keyboardStatus === "DeviceBusy") {
      return "设备被占用";
    }
    if (keyboardStatus === "SleepingOrUnresponsive") {
      return "休眠或无响应";
    }
    if (keyboardStatus === "InvalidResponse") {
      return "响应异常";
    }
    if (keyboardStatus === "Disconnected") {
      return "已断开";
    }
    if (device.receiverStatus === "Present") {
      return "接收器已连接";
    }
    return "已断开";
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
      let message = `请求失败（${response.status}）`;
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
    const isPaused = status.lifecycleState === "Paused";
    const isRunning = status.lifecycleState === "Running";
    const stateKey = isPaused ? "paused" : canonicalState(status.aggregateState);
    const stateLabel = isPaused ? "已暂停" : displayState(status.aggregateState);

    const stateClass = stateKey === "requiresinput" ? "requires-input" : stateKey;
    stateBanner.className = `state-display state-${stateClass}`;
    ledRail.className = `led-rail state-${stateClass}`;
    setText("aggregate-state", stateLabel);
    setText("active-sessions", String(status.activeSessionCount ?? 0));
    setText("last-event", formatTime(status.lastEventAt));
    setText("host-mode", status.lifecycleState === "Faulted"
      ? `灯光接管已停止（${status.faultCode || "未知故障"}）`
      : isPaused
      ? "已恢复原灯效，灯光接管暂停中"
      : status.isPreviewActive
        ? "正在进行三秒灯光预览"
        : "灯光接管已启用");
    setText("hook-status", displayValue(status.hookStatus || "Unknown"));

    const device = status.device || {};
    setText("device-model", displayDeviceModel(device.model));
    setText("device-transport", displayValue(device.transport));
    setText("transport-detail", displayValue(device.transport));
    setText("receiver-status", displayValue(device.receiverStatus));
    setText("keyboard-status", displayValue(device.keyboardStatus));
    setText("keyboard-detail", displayValue(device.keyboardStatus));
    setText("firmware-version", device.firmwareVersion);
    setText("device-identity", device.deviceIdentity);
    setText("interface-fingerprint", device.interfaceFingerprint || "未知");
    setText("support-status", displayValue(device.supportStatus || "Unknown"));
    setText("live-state", deviceConnectionLabel(device));

    const error = document.getElementById("device-error");
    const diagnosticCode = status.faultCode || device.lastErrorCode;
    error.hidden = !diagnosticCode;
    error.textContent = diagnosticCode ? `诊断代码：${diagnosticCode}` : "";

    pauseButton.querySelector(".action-title").textContent = isPaused ? "恢复接管" : "暂停并恢复";
    pauseButton.querySelector(".action-description").textContent = isPaused
      ? "恢复按状态驱动的侧灯"
      : "恢复原灯效并停止更新";
    pauseButton.disabled = !(isRunning || isPaused);
    document.getElementById("preview-button").disabled = !isRunning;
  }

  function setStyle(prefix, style) {
    const color = String(style.color || "#000000").toUpperCase();
    const brightness = Number(style.brightness ?? 0);
    const effect = String(style.effect || "static").toLowerCase();
    const speed = Number(style.speed ?? 1);
    document.getElementById(`${prefix}-color`).value = color;
    document.getElementById(`${prefix}-hex`).value = color;
    document.getElementById(`${prefix}-brightness`).value = brightness;
    document.getElementById(`${prefix}-brightness-value`).textContent = `${brightness}%`;
    document.getElementById(`${prefix}-effect`).value = effect;
    document.getElementById(`${prefix}-speed`).value = speed;
    syncStyleSpeed(prefix);
  }

  function renderSettings(settings) {
    setStyle("thinking", settings.thinking);
    setStyle("requires-input", settings.requiresInput);
    setStyle("complete", settings.complete);
    setStyle("interrupted", settings.interrupted || {
      color: "#FF3B30",
      brightness: 100,
      effect: "static",
      speed: 1
    });
    document.getElementById("complete-hold-seconds").value = settings.completeHoldSeconds;
    document.getElementById("launch-at-sign-in").checked = Boolean(settings.launchAtSignIn);
    document.getElementById("keep-awake-policy").value = settings.keepAwakePolicy || "disabled";
    document.getElementById("keep-awake-region").value = settings.keepAwakeRegion || "sideLights";
    document.getElementById("keep-awake-refresh-seconds").value = settings.keepAwakeRefreshSeconds || 60;
  }

  function readStyle(prefix) {
    return {
      color: document.getElementById(`${prefix}-hex`).value.trim().toUpperCase(),
      brightness: Number(document.getElementById(`${prefix}-brightness`).value),
      effect: document.getElementById(`${prefix}-effect`).value,
      speed: Number(document.getElementById(`${prefix}-speed`).value)
    };
  }

  function readSettings() {
    return {
      thinking: readStyle("thinking"),
      requiresInput: readStyle("requires-input"),
      complete: readStyle("complete"),
      interrupted: readStyle("interrupted"),
      completeHoldSeconds: Number(document.getElementById("complete-hold-seconds").value),
      launchAtSignIn: document.getElementById("launch-at-sign-in").checked,
      keepAwakePolicy: document.getElementById("keep-awake-policy").value,
      keepAwakeRegion: document.getElementById("keep-awake-region").value,
      keepAwakeRefreshSeconds: Number(document.getElementById("keep-awake-refresh-seconds").value)
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
    if (!sessionDiagnosticsEnabled.checked
      || !sessionDiagnosticStates.has(canonicalState(event.status && event.status.aggregateState))) {
      return;
    }

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
      ? "没有可用的已保存诊断。"
      : `已加载 ${entries.length} 条诊断。`);
  }

  function summarizePersistedDiagnostic(entry) {
    const parts = [];
    const visualState = safeDiagnosticToken(entry.visualState, "");
    const transportFailure = safeDiagnosticToken(entry.transportFailure, "");
    const latency = Number(entry.latencyMilliseconds);

    if (visualState) {
      parts.push(`状态 ${displayState(visualState)}`);
    }
    if (transportFailure) {
      parts.push(`传输 ${transportFailure}`);
    }
    if (entry.latencyMilliseconds !== null
      && entry.latencyMilliseconds !== undefined
      && Number.isSafeInteger(latency)
      && latency >= 0) {
      parts.push(`${latency} ms`);
    }

    return parts.length === 0 ? "无其他字段" : parts.join(" · ");
  }

  function safeDiagnosticToken(value, fallback) {
    const tokenValue = String(value || "").trim();
    return /^[a-z0-9._:-]{1,64}$/i.test(tokenValue) ? tokenValue : fallback;
  }

  function summarizeEventStatus(status) {
    if (!status) {
      return "无状态快照";
    }

    const parts = [
      `状态 ${displayState(status.aggregateState)}`,
      `${Number(status.activeSessionCount ?? 0)} 个活动会话`
    ];
    if (status.lifecycleState === "Paused") {
      parts.push("已暂停");
    }
    if (status.isPreviewActive) {
      parts.push("正在预览");
    }
    return parts.join(" · ");
  }

  function bindStyleInputs(prefix) {
    const color = document.getElementById(`${prefix}-color`);
    const hex = document.getElementById(`${prefix}-hex`);
    const brightness = document.getElementById(`${prefix}-brightness`);
    const output = document.getElementById(`${prefix}-brightness-value`);
    const effect = document.getElementById(`${prefix}-effect`);

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

    effect.addEventListener("change", () => syncStyleSpeed(prefix));
  }

  function syncStyleSpeed(prefix) {
    const effect = document.getElementById(`${prefix}-effect`).value;
    const speed = document.getElementById(`${prefix}-speed`);
    speed.disabled = effect !== "flowing";
    speed.title = speed.disabled ? "NuPhyIO 仅为流光开放速度控制" : "";
  }

  async function runBusy(button, operation) {
    button.disabled = true;
    try {
      await operation();
    } catch (error) {
      showNotice(error.message || "本地命令执行失败。", true);
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
      showNotice("设置已保存。");
    });
  });

  document.getElementById("preview-button").addEventListener("click", event => {
    runBusy(event.currentTarget, async () => {
      const state = document.getElementById("preview-state").value;
      await api(`/api/v1/preview/${encodeURIComponent(state)}`, {
        method: "POST",
        body: {}
      });
      showNotice("预览已开始，三秒后恢复之前状态。");
      scheduleStatusRefresh();
    });
  });

  pauseButton.addEventListener("click", () => {
    runBusy(pauseButton, async () => {
      const status = await api("/api/v1/pause", {
        method: "POST",
        body: { paused: !Boolean(currentStatus && currentStatus.lifecycleState === "Paused") }
      });
      renderStatus(status);
      showNotice(status.lifecycleState === "Paused" ? "灯光接管已暂停。" : "灯光接管已恢复。");
    });
  });

  function syncSessionDiagnostics() {
    const enabled = sessionDiagnosticsEnabled.checked;
    document.getElementById("session-diagnostics-content").hidden = !enabled;
    document.getElementById("clear-session-log").disabled = !enabled;
    if (!enabled) {
      document.getElementById("event-log").replaceChildren();
      setText("diagnostic-count", "0");
    }
  }

  function syncSavedDiagnostics() {
    const enabled = savedDiagnosticsEnabled.checked;
    document.getElementById("saved-diagnostics-content").hidden = !enabled;
    document.getElementById("load-recent-diagnostics").disabled = !enabled;
    if (!enabled) {
      document.getElementById("persisted-event-log").replaceChildren();
      setText("persisted-diagnostic-count", "0");
      setText("persisted-diagnostics-status", "尚未加载已保存日志。");
    }
  }

  function resetOptionalFeatures() {
    sessionDiagnosticsEnabled.checked = false;
    savedDiagnosticsEnabled.checked = false;
    syncSessionDiagnostics();
    syncSavedDiagnostics();
  }

  sessionDiagnosticsEnabled.addEventListener("change", syncSessionDiagnostics);
  savedDiagnosticsEnabled.addEventListener("change", syncSavedDiagnostics);
  window.addEventListener("pageshow", resetOptionalFeatures);
  resetOptionalFeatures();

  installHooksButton.addEventListener("click", event => {
    runBusy(event.currentTarget, async () => {
      const result = await api("/api/v1/hooks/install", {
        method: "POST",
        body: {}
      });
      showNotice(result.message || "Codex Hook 安装请求已完成。", !result.succeeded);
      await refreshStatus();
    });
  });

  ["thinking", "requires-input", "complete", "interrupted"].forEach(bindStyleInputs);

  document.getElementById("clear-session-log").addEventListener("click", () => {
    document.getElementById("event-log").replaceChildren();
    setText("diagnostic-count", "0");
  });

  document.getElementById("load-recent-diagnostics").addEventListener("click", event => {
    if (!savedDiagnosticsEnabled.checked) {
      showNotice("请先勾选启用已保存诊断。", true);
      return;
    }

    runBusy(event.currentTarget, async () => {
      const entries = await api("/api/v1/diagnostics?limit=50");
      renderPersistedDiagnostics(Array.isArray(entries) ? entries : []);
    }).finally(syncSavedDiagnostics);
  });

  Promise.all([
    refreshStatus(),
    api("/api/v1/settings").then(renderSettings)
  ]).catch(error => {
    showNotice(error.message || "无法加载本地控制状态。", true);
  });

  const eventSource = new EventSource("/api/v1/events");
  eventSource.onopen = () => {
    document.getElementById("connection-label").classList.remove("is-disconnected");
    setText("connection-text", "主机已连接");
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
    setText("connection-text", "正在重连");
    if (!currentStatus) {
      setText("live-state", "状态不可用");
    }
  };
})();
