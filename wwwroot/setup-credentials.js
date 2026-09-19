// Read at submission so browser/password-manager autofill cannot leave a stale server binding.
// Never store or log these values. Clear the secret immediately after taking the snapshot.
export function takeCredentials(form) {
    const client = form.elements.namedItem("client-id");
    const secret = form.elements.namedItem("client-secret");
    const credentials = { clientId: client.value, clientSecret: secret.value };
    secret.value = "";
    return credentials;
}
