const state = {
    plan: null,
    activeTab: "overview",
    history: [],
    progressTimer: null
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
        const response = await fetch("/api/plans", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(payload)
        });
        if (!response.ok) {
            throw new Error(await readError(response));
        }
        state.plan = await response.json();
        state.activeTab = "overview";
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
        clearInterval(state.progressTimer);
        return;
    }

    const stages = [
        ["主控 Agent 正在拆解任务", "读取目的地、天数、预算、节奏和个性化约束。"],
        ["专业 Agent 正在并行工作", "行程、酒店、交通与风险专家正在调用各自工具。"],
        ["预算 Agent 正在检查约束", "汇总住宿、交通、餐饮与门票估算，判断是否超支。"],
        ["主控 Agent 正在整合方案", "解决路线、舒适度与预算之间的冲突，生成最终计划。"]
    ];
    let index = 0;
    [elements.progressTitle.textContent, elements.progressDetail.textContent] = stages[index];
    state.progressTimer = setInterval(() => {
        index = Math.min(index + 1, stages.length - 1);
        [elements.progressTitle.textContent, elements.progressDetail.textContent] = stages[index];
    }, 1250);
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
                <p class="card-kicker">STAY RECOMMENDATIONS</p>
                <h3>建议住宿落点</h3>
                <div class="hotel-list">
                    ${plan.hotels.map(hotel => `
                        <div class="hotel-item">
                            <span class="agent-icon">宿</span>
                            <div>
                                <b>${escapeHtml(hotel.name)}</b>
                                <span>${escapeHtml(hotel.city)} · ${escapeHtml(hotel.area)} · ${escapeHtml(hotel.level)}</span>
                            </div>
                            <span class="hotel-price">${money(hotel.pricePerNight)}/晚</span>
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
                            <span>当日估算 ${money(day.estimatedCost)}</span>
                        </div>
                        ${day.activities.map(activity => `
                            <div class="activity">
                                <time>${activity.time}</time>
                                <span class="activity-dot"></span>
                                <div>
                                    <b>${escapeHtml(activity.name)} · ${escapeHtml(activity.area)}</b>
                                    <small>${escapeHtml(activity.description)}</small>
                                </div>
                                <span class="activity-cost">${activity.cost ? money(activity.cost) : "免费"}</span>
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
        ["交通", plan.budget.transport],
        ["住宿", plan.budget.accommodation],
        ["餐饮", plan.budget.food],
        ["门票", plan.budget.tickets],
        ["其他", plan.budget.other]
    ];
    const max = Math.max(...items.map(item => item[1]), 1);
    return `
        <div class="budget-grid">
            <article class="result-card budget-chart">
                <p class="card-kicker">COST BREAKDOWN</p>
                <h3>费用明细</h3>
                <div class="budget-table">
                    ${items.map(([label, value]) => `
                        <div class="budget-row">
                            <span>${label}</span>
                            <div class="mini-bar"><i style="width:${(value / max) * 100}%"></i></div>
                            <b>${money(value)}</b>
                        </div>
                    `).join("")}
                </div>
                <div class="suggestion-list">
                    <ul class="plain-list">
                        ${plan.adjustmentSuggestions.map(item => `<li>${escapeHtml(item)}</li>`).join("")}
                    </ul>
                </div>
            </article>
            <article class="result-card transport-card">
                <p class="card-kicker">TRANSPORT PLAN</p>
                <h3>交通方案</h3>
                <div class="transport-main">
                    <b>${escapeHtml(plan.transport.outboundMode)}</b>
                    <span>${escapeHtml(plan.transport.outboundDescription)}</span>
                </div>
                <div class="budget-row"><span>往返交通</span><div></div><b>${money(plan.transport.outboundCost)}</b></div>
                <div class="budget-row"><span>跨城交通</span><div></div><b>${money(plan.transport.intercityCost)}</b></div>
                <div class="budget-row"><span>市内交通</span><div></div><b>${money(plan.transport.localCost)}</b></div>
                <ul class="plain-list">
                    ${plan.transport.routeNotes.map(note => `<li>${escapeHtml(note)}</li>`).join("")}
                </ul>
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
                        <span>${escapeHtml(agent.summary)}</span>
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
