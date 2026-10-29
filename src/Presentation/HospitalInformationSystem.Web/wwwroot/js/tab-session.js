const storageKey = "MedSoft.TabId";
const validId = /^[a-f0-9]{32}$/;
let initialization;
let releaseLease;

function createId() {
    return crypto.randomUUID().replaceAll("-", "");
}

async function acquireLease(id) {
    return new Promise((resolve, reject) => {
        navigator.locks.request(`MedSoft.TabSession.${id}`, { ifAvailable: true }, async lock => {
            if (lock === null) {
                resolve(false);
                return;
            }
            const released = new Promise(release => { releaseLease = release; });
            resolve(true);
            await released;
        }).catch(reject);
    });
}

async function initialize() {
    let id = sessionStorage.getItem(storageKey);
    if (!validId.test(id ?? "")) id = createId();
    if (navigator.locks) {
        while (!await acquireLease(id)) id = createId();
    } else if (performance.getEntriesByType("navigation")[0]?.type !== "reload") {
        id = createId();
    }
    sessionStorage.setItem(storageKey, id);
    return id;
}

addEventListener("pagehide", () => {
    releaseLease?.();
    releaseLease = undefined;
});

addEventListener("pageshow", event => {
    if (event.persisted) location.reload();
});

export function getTabId() {
    return initialization ??= initialize();
}
