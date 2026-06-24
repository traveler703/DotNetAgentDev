const state = {
    plan: null,
    activeTab: "overview",
    history: [],
    streamPercent: 1,
    streamEventCount: 0,
    streamConnected: false,
    streamAbortController: null,
    systemStatus: null,
    streamTextByMessage: new Map(),
    streamElementByMessage: new Map(),
    selectedBudgetCategory: "transport"
};

const elements = {
    form: document.querySelector("#planningForm"),
    submitButton: document.querySelector("#submitButton"),
    exampleButton: document.querySelector("#exampleButton"),
    formError: document.querySelector("#formError"),
    modeBadge: document.querySelector("#modeBadge"),
    workspace: document.querySelector("#workspace"),
    planTitle: document.querySelector("#planTitle"),
    planMeta: document.querySelector("#planMeta"),
    resultTabs: document.querySelector("#resultTabs"),
    resultContent: document.querySelector("#resultContent"),
    overlay: document.querySelector("#planningOverlay"),
    progressTitle: document.querySelector("#progressTitle"),
    progressDetail: document.querySelector("#progressDetail"),
    streamConnectionState: document.querySelector("#streamConnectionState"),
    streamProgressBar: document.querySelector("#streamProgressBar"),
    streamConversation: document.querySelector("#streamConversation"),
    historyButton: document.querySelector("#historyButton"),
    historyDrawer: document.querySelector("#historyDrawer"),
    drawerBackdrop: document.querySelector("#drawerBackdrop"),
    closeHistory: document.querySelector("#closeHistory"),
    historyList: document.querySelector("#historyList"),
    historyCount: document.querySelector("#historyCount"),
    memoryProfile: document.querySelector("#memoryProfile")
};

document.addEventListener("DOMContentLoaded", async () => {
    setDefaultDate();
    bindEvents();
    await Promise.all([loadStatus(), loadHistory(), loadMemory()]);
});

function bindEvents() {
    elements.form.addEventListener("submit", createPlan);
    elements.exampleButton.addEventListener("click", fillExample);
    elements.resultTabs.addEventListener("click", event => {
        const button = event.target.closest("[data-tab]");
        if (!button) return;
        state.activeTab = button.dataset.tab;
        document.querySelectorAll("[data-tab]").forEach(tab => {
            tab.classList.toggle("active", tab.dataset.tab === state.activeTab);
        });
        renderActiveTab();
    });
    elements.resultContent.addEventListener("click", event => {
        const budgetItem = event.target.closest("[data-budget-category]");
        if (!budgetItem) return;
        state.selectedBudgetCategory = budgetItem.dataset.budgetCategory;
        renderActiveTab();
    });
    elements.resultContent.addEventListener("submit", revisePlan);
    elements.historyButton.addEventListener("click", openHistory);
    elements.closeHistory.addEventListener("click", closeHistory);
    elements.drawerBackdrop.addEventListener("click", closeHistory);
    elements.historyList.addEventListener("click", async event => {
        const item = event.target.closest("[data-plan-id]");
        if (!item) return;
        await loadPlan(item.dataset.planId);
        closeHistory();
    });
}

function setDefaultDate() {
    const date = new Date();
    date.setDate(date.getDate() + 30);
    document.querySelector("#startDate").value = date.toISOString().slice(0, 10);
}

function fillExample() {
    document.querySelector("#departure").value = "上海";
    document.querySelector("#destination").value = "日本";
    document.querySelector("#days").value = "7";
    document.querySelector("#travelers").value = "1";
    document.querySelector("#budget").value = "12000";
    document.querySelector("#preferences").value = "美食、城市漫步、人文、夜景";
    document.querySelector("#notes").value = "不想太赶，希望酒店靠近公共交通，并留出自由活动时间";
    document.querySelector('input[name="pace"][value="relaxed"]').checked = true;
}

async function loadStatus() {
    try {
        const response = await fetch("/api/status");
        const status = await response.json();
        state.systemStatus = status;
        elements.modeBadge.classList.remove("is-loading");
        elements.modeBadge.classList.toggle("is-online", status.mode === "deepseek");
        elements.modeBadge.querySelector("span:last-child").textContent =
            status.mode === "deepseek" ? `DeepSeek · ${status.model}` : "离线演示模式";
        elements.modeBadge.title = status.message;
    } catch {
        elements.modeBadge.querySelector("span:last-child").textContent = "状态未知";
    }
}

async function createPlan(event) {
    event.preventDefault();
    hideError();
    const formData = new FormData(elements.form);
    const payload = {
        userId: formData.get("userId") || "demo-user",
        departure: formData.get("departure"),
        destination: formData.get("destination"),
        startDate: formData.get("startDate") || null,
        days: Number(formData.get("days")),
        travelers: Number(formData.get("travelers")),
        budget: Number(formData.get("budget")),
        preferences: formData.get("preferences") || "",
        pace: formData.get("pace"),
        notes: formData.get("notes") || ""
    };

    setPlanning(true, payload);
    const controller = new AbortController();
    state.streamAbortController = controller;
    const connectionTimeout = window.setTimeout(() => {
        controller.abort(new Error("连接 Agent 流式服务超时，请确认服务正在运行后重试。"));
    }, 15000);
    try {
        const response = await fetch("/api/plans/stream", {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "Accept": "text/event-stream"
            },
            body: JSON.stringify(payload),
            signal: controller.signal
        });
        window.clearTimeout(connectionTimeout);
        if (!response.ok) {
            throw new Error(await readError(response));
        }
        state.streamConnected = true;
        elements.streamConnectionState.textContent = "SSE 已连接";
        state.plan = await consumePlanningStream(response, controller);
        state.activeTab = "overview";
        state.selectedBudgetCategory = "transport";
        renderPlan();
        await Promise.all([loadHistory(), loadMemory()]);
        elements.workspace.scrollIntoView({ behavior: "smooth", block: "start" });
    } catch (error) {
        const message = controller.signal.aborted
            ? controller.signal.reason?.message || "流式连接已中止，请稍后重试。"
            : error.message || "生成方案失败，请稍后重试。";
        showError(message);
    } finally {
        window.clearTimeout(connectionTimeout);
        state.streamAbortController = null;
        setPlanning(false);
    }
}

async function revisePlan(event) {
    const form = event.target.closest("#revisionForm");
    if (!form) return;
    event.preventDefault();
    hideError();

    if (!state.plan?.id) {
        showError("请先生成或打开一份旅行计划，再进行后续修改。");
        return;
    }

    const formData = new FormData(form);
    const instruction = String(formData.get("instruction") || "").trim();
    const inlineError = form.querySelector("[data-revision-error]");
    if (!instruction) {
        inlineError.textContent = "请写下希望 Agent 团队修改的内容。";
        inlineError.hidden = false;
        return;
    }
    inlineError.hidden = true;

    const requestForOverlay = {
        ...state.plan.request,
        previousPlanId: state.plan.id,
        revisionInstruction: instruction
    };
    setPlanning(true, requestForOverlay);
    const controller = new AbortController();
    state.streamAbortController = controller;
    const connectionTimeout = window.setTimeout(() => {
        controller.abort(new Error("连接 Agent 流式服务超时，请确认服务正在运行后重试。"));
    }, 15000);

    try {
        const response = await fetch(`/api/plans/${encodeURIComponent(state.plan.id)}/revise/stream`, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "Accept": "text/event-stream"
            },
            body: JSON.stringify({ instruction }),
            signal: controller.signal
        });
        window.clearTimeout(connectionTimeout);
        if (!response.ok) {
            throw new Error(await readError(response));
        }
        state.streamConnected = true;
        elements.streamConnectionState.textContent = "SSE 已连接";
        state.plan = await consumePlanningStream(response, controller);
        state.activeTab = "trace";
        state.selectedBudgetCategory = "transport";
        renderPlan();
        await Promise.all([loadHistory(), loadMemory()]);
        elements.workspace.scrollIntoView({ behavior: "smooth", block: "start" });
    } catch (error) {
        const message = controller.signal.aborted
            ? controller.signal.reason?.message || "流式连接已中止，请稍后重试。"
            : error.message || "修改方案失败，请稍后重试。";
        showError(message);
    } finally {
        window.clearTimeout(connectionTimeout);
        state.streamAbortController = null;
        setPlanning(false);
    }
}

function setPlanning(active, request = null) {
    elements.submitButton.disabled = active;
    elements.overlay.hidden = !active;
    document.body.classList.toggle("planning-open", active);
    if (!active) {
        return;
    }

    state.streamPercent = 1;
    state.streamEventCount = 0;
    state.streamConnected = false;
    state.streamTextByMessage.clear();
    state.streamElementByMessage.clear();
    elements.progressTitle.textContent = "正在建立 Agent 对话";
    elements.progressDetail.textContent = "模型回复、工具调用和工具返回会在这里即时出现。";
    elements.streamConnectionState.textContent = "正在连接 SSE";
    elements.streamProgressBar.style.width = "1%";
    elements.streamConversation.replaceChildren();
    if (request) {
        appendUserChatMessage(elements.streamConversation, userRequestText(request));
    }
}

async function consumePlanningStream(response, controller) {
    if (!response.body) {
        throw new Error("浏览器不支持流式响应。");
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    let completedPlan = null;

    while (true) {
        const { value, done } = await readStreamChunk(reader, controller);
        buffer += decoder.decode(value || new Uint8Array(), { stream: !done });
        buffer = buffer.replaceAll("\r\n", "\n");

        let separatorIndex;
        while ((separatorIndex = buffer.indexOf("\n\n")) >= 0) {
            const block = buffer.slice(0, separatorIndex);
            buffer = buffer.slice(separatorIndex + 2);
            if (!block.trim()) continue;

            const parsed = parseSseBlock(block);
            if (!parsed.data) continue;
            const streamEvent = JSON.parse(parsed.data);
            handlePlanningEvent(streamEvent);

            if (parsed.event === "completed" || streamEvent.type === "completed") {
                completedPlan = streamEvent.plan;
            }
            if (parsed.event === "error" || streamEvent.type === "error") {
                throw new Error(streamEvent.detail || "流式规划失败。");
            }
        }

        if (done) break;
    }

    if (!completedPlan) {
        throw new Error("流式连接已结束，但没有收到完整旅行方案。");
    }
    return completedPlan;
}

async function readStreamChunk(reader, controller) {
    let timeoutId;
    try {
        return await Promise.race([
            reader.read(),
            new Promise((_, reject) => {
                timeoutId = window.setTimeout(() => {
                    const error = new Error("20 秒未收到流式心跳，连接可能已中断。");
                    controller.abort(error);
                    reject(error);
                }, 20000);
            })
        ]);
    } finally {
        window.clearTimeout(timeoutId);
    }
}

function parseSseBlock(block) {
    let event = "message";
    const data = [];
    for (const line of block.split("\n")) {
        if (line.startsWith("event:")) {
            event = line.slice(6).trim();
        } else if (line.startsWith("data:")) {
            data.push(line.slice(5).trimStart());
        }
    }
    return { event, data: data.join("\n") };
}

function handlePlanningEvent(streamEvent) {
    state.streamEventCount += 1;
    state.streamConnected = true;
    elements.streamConnectionState.textContent = "SSE 已连接";
    if (streamEvent.type === "heartbeat") {
        elements.progressDetail.textContent = streamEvent.detail || "连接正常，Agent 仍在工作。";
        return;
    }

    if (streamEvent.percent != null) {
        state.streamPercent = Math.max(state.streamPercent, streamEvent.percent);
    } else if (streamEvent.type === "delta") {
        state.streamPercent = Math.min(94, state.streamPercent + 0.15);
    } else if (streamEvent.type === "trace") {
        state.streamPercent = Math.min(95, state.streamPercent + 2);
    }
    elements.streamProgressBar.style.width = `${state.streamPercent}%`;

    if (streamEvent.type === "delta") {
        upsertLiveAgentAnswer(streamEvent, false);
        return;
    }

    elements.progressTitle.textContent = streamEvent.title || "Agent 正在工作";
    elements.progressDetail.textContent = streamStatusDescription(streamEvent);

    if (isToolEvent(streamEvent)) {
        appendToolChatMessage(elements.streamConversation, streamEvent);
    } else if (streamEvent.phase === "FinalAnswer") {
        upsertLiveAgentAnswer(streamEvent, true);
    } else {
        appendAgentChatMessage(elements.streamConversation, streamEvent);
    }
    scrollConversationToEnd(elements.streamConversation);
}

function renderPlan() {
    if (!state.plan) return;
    const plan = state.plan;
    elements.workspace.hidden = false;
    elements.planTitle.textContent = plan.title;
    elements.planMeta.innerHTML = `
        ${formatDate(plan.request.startDate)} 出发 · ${plan.request.travelers} 人 ·
        <strong>${plan.modelMode === "deepseek" ? "DeepSeek 在线推理" : "离线确定性推理"}</strong>
    `;
    document.querySelectorAll("[data-tab]").forEach(tab => {
        tab.classList.toggle("active", tab.dataset.tab === state.activeTab);
    });
    renderActiveTab();
}

function renderActiveTab() {
    if (!state.plan) return;
    const renderers = {
        overview: renderOverview,
        itinerary: renderItinerary,
        budget: renderBudget,
        risks: renderRisks,
        trace: renderTrace
    };
    elements.resultContent.innerHTML = renderers[state.activeTab]();
}

function renderOverview() {
    const plan = state.plan;
    const percent = Math.min(115, (plan.budget.total / plan.budget.budgetLimit) * 100);
    const highlights = plan.days.flatMap(day => day.activities
        .filter(activity => !["交通", "餐饮"].includes(activity.category))
        .map(activity => ({ ...activity, day: day.day, city: day.city })))
        .filter((activity, index, items) =>
            items.findIndex(item => item.name === activity.name) === index)
        .slice(0, 6);
    return `
        <div class="overview-grid">
            <article class="result-card summary-card">
                <p class="card-kicker">COORDINATOR SUMMARY</p>
                <h3>${escapeHtml(plan.summary)}</h3>
                <div class="summary-metrics">
                    <div><b>${plan.request.days} 天</b><span>旅行时长</span></div>
                    <div><b>${unique(plan.days.map(day => day.city)).length} 城</b><span>路线范围</span></div>
                    <div><b>${plan.days.reduce((sum, day) => sum + day.activities.length, 0)} 项</b><span>计划活动</span></div>
                    <div><b>${plan.trace.filter(step => step.phase === "Action").length} 次</b><span>工具行动</span></div>
                </div>
            </article>
            <article class="result-card budget-status-card">
                <p class="card-kicker">BUDGET STATUS</p>
                <div class="budget-number ${plan.budget.isOverBudget ? "over" : ""}">${money(plan.budget.total)}</div>
                <p class="subtle">总预算 ${money(plan.budget.budgetLimit)} ·
                    ${plan.budget.isOverBudget ? `超支 ${money(Math.abs(plan.budget.remaining))}` : `余量 ${money(plan.budget.remaining)}`}
                </p>
                <div class="progress-bar"><i class="${plan.budget.isOverBudget ? "over" : ""}" style="width:${percent}%"></i></div>
                ${plan.planningRevisionCount
                    ? `<p class="revision-note">已因首次方案超过预算 110% 自动完成第 ${plan.planningRevisionCount + 1} 轮规划。</p>`
                    : ""}
            </article>
            <article class="result-card agent-card">
                <p class="card-kicker">AGENT TEAM</p>
                <h3>六位专家如何分工</h3>
                <div class="agent-list">
                    ${plan.agentContributions.map((agent, index) => `
                        <div class="agent-item">
                            <span class="agent-icon">${index === 0 ? "主" : index}</span>
                            <div><b>${escapeHtml(agent.agent)}</b><span>${escapeHtml(agent.responsibility)}</span></div>
                            <span class="agent-tools">${agent.toolCallCount} 工具</span>
                        </div>
                    `).join("")}
                </div>
            </article>
            <article class="result-card hotel-card">
                <p class="card-kicker">ITINERARY HIGHLIGHTS</p>
                <h3>行程简述</h3>
                <div class="hotel-list itinerary-highlights">
                    ${highlights.map(item => `
                        <div class="hotel-item">
                            <span class="agent-icon">${String(item.day).padStart(2, "0")}</span>
                            <div>
                                <b>${escapeHtml(item.name)}</b>
                                <span>第 ${item.day} 天 · ${escapeHtml(item.city)} · ${escapeHtml(item.time)}-${escapeHtml(item.endTime || "")}</span>
                            </div>
                            <span class="hotel-price">${escapeHtml(item.category)}</span>
                        </div>
                    `).join("")}
                </div>
            </article>
            <article class="result-card capability-card">
                <p class="card-kicker">IMPLEMENTATION EVIDENCE</p>
                <h3>项目能力运行证据</h3>
                <div class="capability-grid">
                    <div><b>SSE 流式输出</b><span>本次接收 ${state.streamEventCount} 个事件</span></div>
                    <div><b>多 Agent 协作</b><span>${plan.agentContributions.length} 个角色完成分工</span></div>
                    <div><b>工具调用</b><span>${plan.agentContributions.reduce((sum, item) => sum + item.toolCallCount, 0)} 次 Action / Observation</span></div>
                    <div><b>MCP Server</b><span>${escapeHtml(state.systemStatus?.mcp?.endpoint || "/mcp")} · ${state.systemStatus?.mcp?.tools || 9} tools</span></div>
                    <div><b>长期记忆</b><span>方案与用户偏好已写入 App_Data</span></div>
                    <div><b>模型模式</b><span>${plan.modelMode === "deepseek" ? "DeepSeek API 在线推理" : "离线确定性降级"}</span></div>
                </div>
            </article>
        </div>
    `;
}

function renderItinerary() {
    return `
        <div class="itinerary-list">
            ${state.plan.days.map(day => `
                <article class="day-card">
                    <div class="day-label">
                        <strong>${String(day.day).padStart(2, "0")}</strong>
                        <span>${formatMonthDay(day.date)}</span>
                        <small>${escapeHtml(day.city)}</small>
                    </div>
                    <div class="day-body">
                        <div class="day-header">
                            <div><h3>${escapeHtml(day.theme)}</h3><span>${escapeHtml(day.paceNote)}</span></div>
                            <div class="day-cost-summary">
                                <b>当日估算 ${money(day.estimatedCost)}</b>
                                <span>交通 ${money(day.costBreakdown?.transport)}</span>
                                <span>门票 ${money(day.costBreakdown?.tickets)}</span>
                                <span>餐饮 ${money(day.costBreakdown?.food)}</span>
                            </div>
                        </div>
                        ${day.activities.map(activity => `
                            <div class="activity">
                                <time>${escapeHtml(activity.time)}<small>${activity.endTime ? `-${escapeHtml(activity.endTime)}` : ""}</small></time>
                                <span class="activity-dot"></span>
                                <div>
                                    <b>${escapeHtml(activity.name)}</b>
                                    <em>${escapeHtml(activity.venue || activity.area)} · ${escapeHtml(activity.category)}</em>
                                    <small>${escapeHtml(activity.description)}</small>
                                    ${activity.sourceUrl
                                        ? `<a class="source-link" href="${safeUrl(activity.sourceUrl)}" target="_blank" rel="noreferrer">来源：${escapeHtml(activity.sourceTitle || "网页资料")}</a>`
                                        : ""}
                                </div>
                                <div class="activity-cost">
                                    <b>${activity.cost ? money(activity.cost) : "免费"}</b>
                                    ${renderActivityCosts(activity.costBreakdown)}
                                </div>
                            </div>
                        `).join("")}
                    </div>
                </article>
            `).join("")}
        </div>
    `;
}

function renderBudget() {
    const plan = state.plan;
    const items = [
        ["transport", "交通", plan.budget.transport],
        ["accommodation", "住宿", plan.budget.accommodation],
        ["food", "餐饮", plan.budget.food],
        ["tickets", "门票", plan.budget.tickets],
        ["other", "其他", plan.budget.other]
    ];
    const max = Math.max(...items.map(item => item[2]), 1);
    const selected = items.find(item => item[0] === state.selectedBudgetCategory) || items[0];
    const details = getExpenseDetails(plan, selected[0]);
    return `
        <div class="budget-grid">
            <article class="result-card budget-chart">
                <p class="card-kicker">COST BREAKDOWN</p>
                <h3>各项费用</h3>
                <div class="budget-table">
                    ${items.map(([category, label, value]) => `
                        <button class="budget-row budget-select ${category === selected[0] ? "active" : ""}"
                                type="button" data-budget-category="${category}">
                            <span>${label}</span>
                            <div class="mini-bar"><i style="width:${(value / max) * 100}%"></i></div>
                            <b>${money(value)}</b>
                        </button>
                    `).join("")}
                </div>
                <div class="suggestion-list">
                    <ul class="plain-list">
                        ${plan.adjustmentSuggestions.map(item => `<li>${escapeHtml(item)}</li>`).join("")}
                    </ul>
                </div>
            </article>
            <article class="result-card transport-card expense-detail-card">
                <p class="card-kicker">EXPENSE DETAILS</p>
                <h3>${selected[1]}支出明细</h3>
                <div class="expense-total">
                    <span>${details.length} 笔支出</span>
                    <b>${money(selected[2])}</b>
                </div>
                <div class="expense-list">
                    ${details.length ? details.map(detail => `
                        <div class="expense-item">
                            <div>
                                <b>${escapeHtml(detail.label)}</b>
                                <span>${detail.date ? `${formatMonthDay(detail.date)} · ` : ""}${escapeHtml(detail.description)}</span>
                            </div>
                            <strong>${money(detail.amount)}</strong>
                        </div>
                    `).join("") : `<p class="subtle">该历史方案没有保存分项数据，请重新生成方案。</p>`}
                </div>
            </article>
        </div>
    `;
}

function renderRisks() {
    const labels = { high: "重要", medium: "注意", low: "提示" };
    return `
        <div class="risk-list">
            ${state.plan.risks.map(risk => `
                <article class="risk-card">
                    <span class="risk-level ${risk.level}">${labels[risk.level] || risk.level}</span>
                    <div class="risk-copy">
                        <h3>${escapeHtml(risk.title)}</h3>
                        <p>${escapeHtml(risk.detail)}</p>
                        <strong>建议：${escapeHtml(risk.recommendation)}</strong>
                        ${risk.sources?.length ? `
                            <div class="risk-sources">
                                ${risk.sources.map(source => `
                                    <a href="${safeUrl(source.url)}" target="_blank" rel="noreferrer">
                                        ${escapeHtml(source.title)}${source.publishedAt ? ` · ${escapeHtml(source.publishedAt)}` : ""}
                                    </a>
                                `).join("")}
                            </div>
                        ` : ""}
                    </div>
                </article>
            `).join("")}
        </div>
    `;
}

function renderTrace() {
    const plan = state.plan;
    return `
        <div class="trace-chat-page">
            <header class="trace-chat-heading">
                <div>
                    <p class="card-kicker">AGENT CONVERSATION</p>
                    <h3>Agent 协作对话</h3>
                </div>
                <span>${plan.trace.length} 条消息 · ${plan.agentContributions.reduce((sum, item) => sum + item.toolCallCount, 0)} 次工具</span>
            </header>
            <div class="agent-conversation trace-conversation">
                ${renderUserRequestMessage(plan.request)}
                ${plan.trace.map(renderTraceChatEntry).join("")}
            </div>
            ${renderRevisionComposer(plan)}
        </div>
    `;
}

function upsertLiveAgentAnswer(streamEvent, finalized) {
    const messageId = streamEvent.messageId || `answer-${streamEvent.agent}`;
    const previous = state.streamTextByMessage.get(messageId) || "";
    const content = finalized
        ? (streamEvent.detail || previous)
        : `${previous}${streamEvent.detail || ""}`;
    state.streamTextByMessage.set(messageId, content);

    let body = state.streamElementByMessage.get(messageId);
    if (!body) {
        const article = createAgentChatElement(streamEvent, "answer");
        body = article.querySelector(".chat-message-body");
        state.streamElementByMessage.set(messageId, body);
        elements.streamConversation.append(article);
    }
    body.innerHTML = renderMarkdown(content || "正在组织回复…");
    body.closest(".agent-chat-message")?.classList.toggle("is-streaming", !finalized);
    scrollConversationToEnd(elements.streamConversation);
}

function appendUserChatMessage(container, text) {
    const article = document.createElement("article");
    article.className = "user-chat-message";
    const label = document.createElement("span");
    label.textContent = "你";
    const body = document.createElement("p");
    body.textContent = text;
    article.append(label, body);
    container.append(article);
}

function appendAgentChatMessage(container, streamEvent) {
    const article = createAgentChatElement(streamEvent, streamEvent.phase?.toLowerCase() || "progress");
    article.querySelector(".chat-message-body").innerHTML =
        renderMarkdown(streamEvent.detail || streamEvent.title || "");
    container.append(article);
}

function createAgentChatElement(streamEvent, variant) {
    const article = document.createElement("article");
    article.className = `agent-chat-message ${variant}`;
    const avatar = document.createElement("span");
    avatar.className = `chat-avatar ${agentClass(streamEvent.agent)}`;
    avatar.textContent = agentInitial(streamEvent.agent);
    const content = document.createElement("div");
    content.className = "agent-chat-content";
    const header = document.createElement("header");
    const name = document.createElement("b");
    name.textContent = streamEvent.agent || "系统";
    const meta = document.createElement("span");
    meta.textContent = `${phaseLabel(streamEvent.phase)} · ${formatTime(streamEvent.timestamp || new Date().toISOString())}`;
    header.append(name, meta);
    const body = document.createElement("div");
    body.className = "chat-message-body markdown-body";
    content.append(header, body);
    article.append(avatar, content);
    return article;
}

function appendToolChatMessage(container, streamEvent) {
    const article = document.createElement("article");
    const successClass = streamEvent.success === false ? "failed" : "";
    article.className = `tool-chat-message ${streamEvent.phase.toLowerCase()} ${successClass}`;
    const icon = document.createElement("span");
    icon.className = "tool-chat-icon";
    icon.textContent = streamEvent.phase === "Action" ? "↗" : streamEvent.success === false ? "!" : "✓";
    const content = document.createElement("div");
    const header = document.createElement("header");
    const title = document.createElement("b");
    title.textContent = streamEvent.phase === "Action"
        ? `${streamEvent.agent} 调用 ${streamEvent.toolName || streamEvent.title.replace("调用 ", "")}`
        : `${streamEvent.toolName || streamEvent.title.replace(" 返回结果", "")} 返回结果`;
    const time = document.createElement("span");
    time.textContent = formatTime(streamEvent.timestamp || new Date().toISOString());
    header.append(title, time);
    const details = document.createElement("details");
    if (streamEvent.phase === "Action") details.open = true;
    const summary = document.createElement("summary");
    summary.textContent = streamEvent.phase === "Action" ? "查看调用参数" : "查看工具输出";
    const pre = document.createElement("pre");
    pre.textContent = prettyToolDetail(streamEvent.detail);
    details.append(summary, pre);
    content.append(header, details);
    article.append(icon, content);
    container.append(article);
}

function renderUserRequestMessage(request) {
    const detail = userRequestText(request);
    return `<article class="user-chat-message"><span>你</span><p>${escapeHtml(detail)}</p></article>`;
}

function renderRevisionComposer(plan) {
    return `
        <form id="revisionForm" class="revision-composer">
            <div>
                <p class="card-kicker">PLAN REVISION</p>
                <h3>继续修改这份旅行计划</h3>
                <p>可以要求增加或删除景点、调整某个景点到第几天上午/下午/晚上，或压缩某类费用。</p>
            </div>
            <label for="revisionInstruction">本次修改要求</label>
            <textarea id="revisionInstruction" name="instruction" rows="3"
                placeholder="例如：把滨海湾花园改到第 2 天晚上，删除购物中心，增加一次当地小吃街体验。"></textarea>
            <p class="inline-error" data-revision-error hidden></p>
            <footer>
                <span>将基于当前方案 ${escapeHtml(plan.id.slice(0, 8))} 重新运行 Agent 团队并保存新版本。</span>
                <button type="submit">重新运行 Agent 团队</button>
            </footer>
        </form>
    `;
}

function userRequestText(request) {
    if (request.revisionInstruction) {
        return `请基于上一版旅行计划进行修改：${request.revisionInstruction}。`
            + ` 原始需求为从${request.departure}前往${request.destination}的${request.days}天旅行，`
            + `${request.travelers}人，总预算${money(request.budget)}。`;
    }

    return `请规划从${request.departure}前往${request.destination}的${request.days}天旅行，`
        + `${request.travelers}人，总预算${money(request.budget)}。`
        + `${request.preferences ? `偏好：${request.preferences}。` : ""}`
        + `${request.notes ? `补充要求：${request.notes}。` : ""}`;
}

function renderTraceChatEntry(step) {
    if (isStoredToolTrace(step)) {
        const toolName = step.title
            .replace(/^调用\s+/, "")
            .replace(/\s+返回结果$/, "")
            .replace(/\s+调用失败$/, "");
        return `
            <article class="tool-chat-message ${step.phase.toLowerCase()}">
                <span class="tool-chat-icon">${step.phase === "Action" ? "↗" : "✓"}</span>
                <div>
                    <header>
                        <b>${escapeHtml(step.phase === "Action"
                            ? `${step.agent} 调用 ${toolName}`
                            : `${toolName} 返回结果`)}</b>
                        <span>${formatTime(step.timestamp)}</span>
                    </header>
                    <details ${step.phase === "Action" ? "open" : ""}>
                        <summary>${step.phase === "Action" ? "查看调用参数" : "查看工具输出"}</summary>
                        <pre>${escapeHtml(prettyToolDetail(step.detail))}</pre>
                    </details>
                </div>
            </article>
        `;
    }

    return `
        <article class="agent-chat-message ${step.phase.toLowerCase()}">
            <span class="chat-avatar ${agentClass(step.agent)}">${agentInitial(step.agent)}</span>
            <div class="agent-chat-content">
                <header>
                    <b>${escapeHtml(step.agent)}</b>
                    <span>${phaseLabel(step.phase)} · ${formatTime(step.timestamp)}</span>
                </header>
                <div class="chat-message-body markdown-body">${renderMarkdown(step.detail)}</div>
            </div>
        </article>
    `;
}

function isToolEvent(streamEvent) {
    return Boolean(streamEvent.toolName)
        && (streamEvent.phase === "Action" || streamEvent.phase === "Observation");
}

function streamStatusDescription(streamEvent) {
    if (streamEvent.phase === "Action" && streamEvent.toolName) {
        return `${streamEvent.agent} 正在调用 ${streamEvent.toolName} 获取可验证数据。`;
    }
    if (streamEvent.phase === "Observation" && streamEvent.toolName) {
        return streamEvent.success === false
            ? `${streamEvent.toolName} 调用失败，Agent 将根据已有信息继续处理。`
            : `${streamEvent.toolName} 已返回结果，正在交给 ${streamEvent.agent} 整理。`;
    }
    if (streamEvent.phase === "FinalAnswer") {
        return `${streamEvent.agent} 已提交专业结论。`;
    }
    return streamEvent.detail || "Agent 团队正在协作。";
}

function isStoredToolTrace(step) {
    return (step.phase === "Action" && /^调用\s+/.test(step.title))
        || (step.phase === "Observation"
            && (/\s+返回结果$/.test(step.title) || /\s+调用失败$/.test(step.title)));
}

function prettyToolDetail(value) {
    if (!value) return "无附加内容";
    try {
        return JSON.stringify(JSON.parse(value), null, 2);
    } catch {
        return value;
    }
}

function agentInitial(agent) {
    if (!agent || agent === "系统") return "系";
    if (agent.includes("主控")) return "主";
    if (agent.includes("行程")) return "行";
    if (agent.includes("酒店")) return "住";
    if (agent.includes("交通")) return "交";
    if (agent.includes("预算")) return "预";
    if (agent.includes("风险")) return "险";
    if (agent.includes("记忆")) return "忆";
    return agent.slice(0, 1);
}

function agentClass(agent = "") {
    if (agent.includes("主控")) return "coordinator";
    if (agent.includes("行程")) return "itinerary";
    if (agent.includes("酒店")) return "hotel";
    if (agent.includes("交通")) return "transport";
    if (agent.includes("预算")) return "budget";
    if (agent.includes("风险")) return "risk";
    return "system";
}

function phaseLabel(phase) {
    return {
        Thought: "任务分析",
        FinalAnswer: "Agent 回复",
        Progress: "系统消息",
        Start: "系统消息",
        Connected: "连接成功",
        Heartbeat: "连接心跳",
        Memory: "长期记忆",
        Completed: "已完成",
        Model: "正在回复"
    }[phase] || phase || "消息";
}

function scrollConversationToEnd(container) {
    requestAnimationFrame(() => {
        container.scrollTop = container.scrollHeight;
    });
}

async function loadHistory() {
    try {
        const response = await fetch("/api/plans?userId=demo-user");
        state.history = response.ok ? await response.json() : [];
        elements.historyCount.textContent = String(state.history.length);
        renderHistory();
    } catch {
        state.history = [];
        renderHistory();
    }
}

function renderHistory() {
    if (!state.history.length) {
        const template = document.querySelector("#emptyHistoryTemplate");
        elements.historyList.replaceChildren(template.content.cloneNode(true));
        return;
    }
    elements.historyList.innerHTML = state.history.map(plan => `
        <button class="history-item" type="button" data-plan-id="${plan.id}">
            <h3>${escapeHtml(plan.title)}</h3>
            <p>${escapeHtml(plan.request.departure)} → ${escapeHtml(plan.request.destination)} · ${formatDate(plan.request.startDate)}</p>
            <footer>
                <span>${money(plan.budget.total)}</span>
                <span>${plan.request.days} 天 · ${formatCreatedAt(plan.createdAt)}</span>
            </footer>
        </button>
    `).join("");
}

async function loadMemory() {
    try {
        const response = await fetch("/api/memory/demo-user");
        const profile = await response.json();
        const preferences = profile.preferences?.length
            ? profile.preferences
            : ["等待首次规划"];
        const paces = profile.travelPaces?.length
            ? profile.travelPaces
            : [];
        const notes = profile.notes?.length
            ? profile.notes
            : [];
        elements.memoryProfile.innerHTML = `
            <h3>用户偏好记忆 · ${profile.planCount} 份方案</h3>
            <div class="memory-chips">${preferences.map(item => `<span>${escapeHtml(item)}</span>`).join("")}</div>
            ${paces.length
                ? `<p class="memory-label">历史节奏</p><div class="memory-chips">${paces.map(item => `<span>${escapeHtml(item)}</span>`).join("")}</div>`
                : ""}
            ${notes.length
                ? `<p class="memory-label">备注与约束</p><div class="memory-notes">${notes.map(item => `<span>${escapeHtml(item)}</span>`).join("")}</div>`
                : ""}
            ${profile.averageBudgetPerDay
                ? `<p class="subtle">历史日均预算 ${money(profile.averageBudgetPerDay)}</p>`
                : ""}
        `;
    } catch {
        elements.memoryProfile.innerHTML = "<h3>长期记忆暂不可用</h3>";
    }
}

async function loadPlan(id) {
    const response = await fetch(`/api/plans/${encodeURIComponent(id)}`);
    if (!response.ok) return;
    state.plan = await response.json();
    state.activeTab = "overview";
    state.selectedBudgetCategory = "transport";
    renderPlan();
    elements.workspace.scrollIntoView({ behavior: "smooth", block: "start" });
}

function openHistory() {
    elements.historyDrawer.classList.add("open");
    elements.historyDrawer.setAttribute("aria-hidden", "false");
    elements.drawerBackdrop.hidden = false;
}

function closeHistory() {
    elements.historyDrawer.classList.remove("open");
    elements.historyDrawer.setAttribute("aria-hidden", "true");
    elements.drawerBackdrop.hidden = true;
}

async function readError(response) {
    try {
        const body = await response.json();
        if (body.errors) {
            return Object.values(body.errors).flat().join(" ");
        }
        return body.detail || body.title || "请求失败。";
    } catch {
        return `请求失败（HTTP ${response.status}）。`;
    }
}

function showError(message) {
    elements.formError.textContent = message;
    elements.formError.hidden = false;
}

function hideError() {
    elements.formError.hidden = true;
    elements.formError.textContent = "";
}

function money(value) {
    return new Intl.NumberFormat("zh-CN", {
        style: "currency",
        currency: "CNY",
        maximumFractionDigits: 0
    }).format(value || 0);
}

function formatDate(value) {
    if (!value) return "日期待定";
    return new Intl.DateTimeFormat("zh-CN", {
        year: "numeric",
        month: "long",
        day: "numeric"
    }).format(new Date(`${value}T00:00:00`));
}

function formatMonthDay(value) {
    return new Intl.DateTimeFormat("zh-CN", {
        month: "2-digit",
        day: "2-digit"
    }).format(new Date(`${value}T00:00:00`));
}

function formatTime(value) {
    return new Intl.DateTimeFormat("zh-CN", {
        hour: "2-digit",
        minute: "2-digit",
        second: "2-digit"
    }).format(new Date(value));
}

function formatCreatedAt(value) {
    return new Intl.DateTimeFormat("zh-CN", {
        month: "2-digit",
        day: "2-digit"
    }).format(new Date(value));
}

function shortPhase(phase) {
    return { Thought: "T", Action: "A", Observation: "O", FinalAnswer: "F" }[phase] || "·";
}

function renderActivityCosts(costs) {
    if (!costs) return "";
    const entries = [
        ["交通", costs.transport],
        ["门票", costs.tickets],
        ["餐饮", costs.food],
        ["其他", costs.other]
    ].filter(([, value]) => Number(value) > 0);
    return entries.map(([label, value]) => `<small>${label} ${money(value)}</small>`).join("");
}

function getExpenseDetails(plan, category) {
    if (plan.expenseDetails?.length) {
        return plan.expenseDetails.filter(detail => detail.category === category);
    }
    if (category === "transport") {
        return [
            { label: "往返交通", description: plan.transport.outboundDescription, amount: plan.transport.outboundCost },
            { label: "跨城交通", description: "城市间移动估算", amount: plan.transport.intercityCost },
            { label: "市内交通", description: "每日公共交通与接驳", amount: plan.transport.localCost }
        ].filter(item => item.amount > 0);
    }
    return [];
}

function renderMarkdown(value) {
    const escaped = escapeHtml(value || "");
    const lines = escaped.split(/\r?\n/);
    const output = [];
    let listType = null;
    let inCode = false;
    let codeLines = [];
    const closeList = () => {
        if (listType) {
            output.push(`</${listType}>`);
            listType = null;
        }
    };

    for (let index = 0; index < lines.length; index++) {
        const rawLine = lines[index];
        const line = rawLine.trim();
        if (line.startsWith("```")) {
            closeList();
            if (inCode) {
                output.push(`<pre><code>${codeLines.join("\n")}</code></pre>`);
                codeLines = [];
            }
            inCode = !inCode;
            continue;
        }
        if (inCode) {
            codeLines.push(rawLine);
            continue;
        }

        const nextLine = lines[index + 1]?.trim() || "";
        if (line.includes("|") && /^\|?[\s:|-]+\|[\s:|-|]*$/.test(nextLine)) {
            closeList();
            const headers = markdownTableCells(line);
            output.push("<div class=\"markdown-table-wrap\"><table><thead><tr>");
            output.push(headers.map(cell => `<th>${renderInlineMarkdown(cell)}</th>`).join(""));
            output.push("</tr></thead><tbody>");
            index += 2;
            while (index < lines.length && lines[index].includes("|") && lines[index].trim()) {
                const cells = markdownTableCells(lines[index]);
                output.push("<tr>");
                output.push(cells.map(cell => `<td>${renderInlineMarkdown(cell)}</td>`).join(""));
                output.push("</tr>");
                index++;
            }
            output.push("</tbody></table></div>");
            index--;
            continue;
        }

        const unordered = line.match(/^[-*]\s+(.+)$/);
        const ordered = line.match(/^\d+[.)]\s+(.+)$/);
        if (unordered || ordered) {
            const desiredType = ordered ? "ol" : "ul";
            if (listType !== desiredType) {
                closeList();
                output.push(`<${desiredType}>`);
                listType = desiredType;
            }
            output.push(`<li>${renderInlineMarkdown((unordered || ordered)[1])}</li>`);
            continue;
        }

        closeList();
        if (!line) continue;
        const heading = line.match(/^(#{1,4})\s+(.+)$/);
        if (heading) {
            const level = Math.min(5, heading[1].length + 2);
            output.push(`<h${level}>${renderInlineMarkdown(heading[2])}</h${level}>`);
        } else if (/^---+$/.test(line)) {
            output.push("<hr>");
        } else if (line.startsWith("&gt;")) {
            output.push(`<blockquote>${renderInlineMarkdown(line.replace(/^&gt;\s?/, ""))}</blockquote>`);
        } else {
            output.push(`<p>${renderInlineMarkdown(line)}</p>`);
        }
    }
    closeList();
    if (inCode) {
        output.push(`<pre><code>${codeLines.join("\n")}</code></pre>`);
    }
    return output.join("");
}

function markdownTableCells(line) {
    return line
        .replace(/^\|/, "")
        .replace(/\|$/, "")
        .split("|")
        .map(cell => cell.trim());
}

function renderInlineMarkdown(value) {
    return value
        .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
        .replace(/~~(.+?)~~/g, "<del>$1</del>")
        .replace(/`([^`]+)`/g, "<code>$1</code>")
        .replace(/\[([^\]]+)]\((https?:\/\/[^)\s]+)\)/g,
            '<a href="$2" target="_blank" rel="noreferrer">$1</a>');
}

function safeUrl(value) {
    try {
        const url = new URL(value, window.location.origin);
        return ["http:", "https:"].includes(url.protocol) ? escapeHtml(url.href) : "#";
    } catch {
        return "#";
    }
}

function unique(items) {
    return [...new Set(items)];
}

function escapeHtml(value) {
    return String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#039;");
}
