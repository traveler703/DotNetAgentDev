const state = {
    plan: null,
    activeTab: "overview",
    history: [],
    streamPercent: 1,
    streamDeltaByAgent: new Map(),
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
    streamProgressBar: document.querySelector("#streamProgressBar"),
    streamFeed: document.querySelector("#streamFeed"),
    streamDelta: document.querySelector("#streamDelta"),
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

    setPlanning(true);
    try {
        const response = await fetch("/api/plans/stream", {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                "Accept": "text/event-stream"
            },
            body: JSON.stringify(payload)
        });
        if (!response.ok) {
            throw new Error(await readError(response));
        }
        state.plan = await consumePlanningStream(response);
        state.activeTab = "overview";
        state.selectedBudgetCategory = "transport";
        renderPlan();
        await Promise.all([loadHistory(), loadMemory()]);
        elements.workspace.scrollIntoView({ behavior: "smooth", block: "start" });
    } catch (error) {
        showError(error.message || "生成方案失败，请稍后重试。");
    } finally {
        setPlanning(false);
    }
}

function setPlanning(active) {
    elements.submitButton.disabled = active;
    elements.overlay.hidden = !active;
    if (!active) {
        return;
    }

    state.streamPercent = 1;
    state.streamDeltaByAgent.clear();
    elements.progressTitle.textContent = "正在建立流式连接";
    elements.progressDetail.textContent = "服务器会持续推送 Agent 决策、工具行动和模型输出。";
    elements.streamProgressBar.style.width = "1%";
    elements.streamFeed.replaceChildren();
    elements.streamDelta.textContent = "";
}

async function consumePlanningStream(response) {
    if (!response.body) {
        throw new Error("浏览器不支持流式响应。");
    }

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    let completedPlan = null;

    while (true) {
        const { value, done } = await reader.read();
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
    if (streamEvent.percent != null) {
        state.streamPercent = Math.max(state.streamPercent, streamEvent.percent);
    } else if (streamEvent.type === "trace") {
        state.streamPercent = Math.min(95, state.streamPercent + 2);
    }
    elements.streamProgressBar.style.width = `${state.streamPercent}%`;

    if (streamEvent.type === "delta") {
        const previous = state.streamDeltaByAgent.get(streamEvent.agent) || "";
        const current = `${previous}${streamEvent.detail || ""}`;
        state.streamDeltaByAgent.set(streamEvent.agent, current.slice(-260));
        elements.streamDelta.textContent =
            `${streamEvent.agent}：${state.streamDeltaByAgent.get(streamEvent.agent)}`;
        return;
    }

    elements.progressTitle.textContent = streamEvent.title || "Agent 正在工作";
    elements.progressDetail.textContent = streamEvent.detail || "";

    const item = document.createElement("div");
    item.className = "stream-item";
    const agent = document.createElement("b");
    agent.textContent = streamEvent.agent || "系统";
    const title = document.createElement("span");
    title.textContent = `${streamEvent.phase || "Progress"} · ${streamEvent.title || ""}`;
    item.append(agent, title);
    elements.streamFeed.append(item);

    while (elements.streamFeed.children.length > 8) {
        elements.streamFeed.firstElementChild.remove();
    }
    elements.streamFeed.scrollTop = elements.streamFeed.scrollHeight;
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
        <div class="trace-layout">
            <aside class="trace-agents">
                <p class="card-kicker">OBSERVABILITY</p>
                <h3>Agent 执行概览</h3>
                ${plan.agentContributions.map(agent => `
                    <div class="trace-agent">
                        <b>${escapeHtml(agent.agent)} · ${agent.toolCallCount} 次工具</b>
                        <div class="markdown-body">${renderMarkdown(agent.summary)}</div>
                    </div>
                `).join("")}
            </aside>
            <div class="trace-timeline">
                ${plan.trace.map(step => `
                    <article class="trace-step">
                        <span class="trace-marker ${step.phase.toLowerCase()}">${shortPhase(step.phase)}</span>
                        <div class="trace-copy">
                            <header>
                                <b>${escapeHtml(step.agent)} · ${escapeHtml(step.phase)}</b>
                                <time>${formatTime(step.timestamp)}</time>
                            </header>
                            <h4>${escapeHtml(step.title)}</h4>
                            <p>${escapeHtml(step.detail)}</p>
                        </div>
                    </article>
                `).join("")}
            </div>
        </div>
    `;
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
        elements.memoryProfile.innerHTML = `
            <h3>用户偏好记忆 · ${profile.planCount} 份方案</h3>
            <div class="memory-chips">${preferences.map(item => `<span>${escapeHtml(item)}</span>`).join("")}</div>
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
    let inList = false;
    for (const rawLine of lines) {
        const line = rawLine.trim();
        if (/^[-*]\s+/.test(line)) {
            if (!inList) {
                output.push("<ul>");
                inList = true;
            }
            output.push(`<li>${renderInlineMarkdown(line.replace(/^[-*]\s+/, ""))}</li>`);
            continue;
        }
        if (inList) {
            output.push("</ul>");
            inList = false;
        }
        if (!line) continue;
        const heading = line.match(/^(#{1,4})\s+(.+)$/);
        if (heading) {
            const level = Math.min(5, heading[1].length + 2);
            output.push(`<h${level}>${renderInlineMarkdown(heading[2])}</h${level}>`);
        } else {
            output.push(`<p>${renderInlineMarkdown(line)}</p>`);
        }
    }
    if (inList) output.push("</ul>");
    return output.join("");
}

function renderInlineMarkdown(value) {
    return value
        .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
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
