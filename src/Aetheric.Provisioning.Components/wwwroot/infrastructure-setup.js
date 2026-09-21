function initialize() {
    const root = document.querySelector('[data-infrastructure]');
    if (!root || root.dataset.initialized) return;
    root.dataset.initialized = 'true';
    const forms = [...root.querySelectorAll('form[data-service]')];
    const finish = root.querySelector('[data-finish]');
    if (!finish || forms.length === 0) return;
    const receipts = new Map();
    const revisions = new Map();
    const busy = new Set();
    let saving = false;
    const update = () => {
        root.querySelector('[data-progress]').textContent = `${receipts.size} of ${forms.length} connections verified`;
        finish.disabled = saving || busy.size > 0 || receipts.size !== forms.length;
    };
    const invalidate = form => {
        receipts.delete(form.dataset.service);
        revisions.set(form, (revisions.get(form) || 0) + 1);
        form.querySelector('[role=status]').textContent = 'Changed — test this connection again.';
        update();
    };
    const post = async (url, data) => {
        const response = await fetch(url, { method: 'POST', body: data, credentials: 'same-origin', redirect: 'error' });
        if (response.status === 401 || response.status === 403) throw new Error('Your session expired. Sign in again to resume saved progress.');
        if (!response.headers.get('content-type')?.includes('application/json')) throw new Error('The request could not finish. Reload the page or sign in again.');
        return await response.json();
    };
    for (const form of forms) {
        form.querySelector('fieldset').disabled = false;
        form.addEventListener('input', () => invalidate(form));
        form.addEventListener('change', () => invalidate(form));
        form.querySelector('.reveal-password').addEventListener('click', event => {
            const input = form.elements.password;
            const reveal = input.type === 'password';
            input.type = reveal ? 'text' : 'password';
            event.currentTarget.textContent = reveal ? 'Hide password' : 'Show password';
            event.currentTarget.setAttribute('aria-pressed', String(reveal));
        });
        form.elements.keepPassword?.addEventListener('change', event => {
            form.elements.password.disabled = event.target.checked;
            form.elements.password.required = !event.target.checked;
            form.elements.password.value = '';
        });
        form.addEventListener('submit', async event => {
            event.preventDefault();
            if (busy.has(form) || saving || !form.reportValidity()) return;
            receipts.delete(form.dataset.service);
            const revision = revisions.get(form) || 0;
            const submitted = new FormData(form); // Includes actual password-manager/autofill values.
            const snapshot = JSON.stringify([...submitted]);
            const status = form.querySelector('[role=status]');
            const button = form.querySelector('[data-test]');
            busy.add(form); button.disabled = true; status.textContent = 'Testing connection…'; update();
            try {
                const result = await post('/setup/infrastructure/test', submitted);
                if ((revisions.get(form) || 0) !== revision || JSON.stringify([...new FormData(form)]) !== snapshot) {
                    status.textContent = 'Changed during testing — test this connection again.';
                } else {
                    status.textContent = result.message;
                    if (result.success) {
                        receipts.set(form.dataset.service, result.receipt);
                        // Receipts expire server-side after ten minutes.
                        setTimeout(() => {
                            if (receipts.get(form.dataset.service) === result.receipt) {
                                receipts.delete(form.dataset.service);
                                status.textContent = 'Test expired — test this connection again.'; update();
                            }
                        }, 10 * 60 * 1000);
                    }
                }
            } catch (error) { status.textContent = error.message || 'The test could not finish. Retry.'; }
            finally { busy.delete(form); button.disabled = false; update(); }
        });
    }
    finish.addEventListener('click', async () => {
        if (finish.disabled || saving) return;
        const data = new FormData();
        for (const form of forms) {
            if (!form.reportValidity()) return;
            const system = form.dataset.service;
            for (const [name, value] of new FormData(form)) {
                if (name === '__RequestVerificationToken') data.set(name, value);
                else data.set(`${system}.${name}`, value);
            }
            data.set(`${system}.receipt`, receipts.get(system));
        }
        saving = true;
        forms.forEach(form => { form.querySelector('fieldset').disabled = true; });
        update();
        const status = root.querySelector('[data-save-result]');
        status.textContent = 'Saving your connections…';
        try {
            const result = await post('/setup/infrastructure/save', data);
            if (result.success) {
                forms.forEach(form => { form.elements.password.value = ''; });
                receipts.clear();
                location.assign(result.redirect);
                return;
            }
            status.textContent = result.message;
        } catch (error) { status.textContent = error.message || 'Saving could not finish. Retry; successful saves are retained.'; }
        finally {
            saving = false;
            forms.forEach(form => { form.querySelector('fieldset').disabled = false; });
            update();
        }
    });
    update();
}
initialize();
// Reinitialize when Blazor enhanced navigation loads this page without a full reload.
if (window.Blazor?.addEventListener) Blazor.addEventListener('enhancedload', initialize);
else document.addEventListener('DOMContentLoaded', initialize, { once: true });
