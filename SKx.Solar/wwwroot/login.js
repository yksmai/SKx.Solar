"use strict";

const form = document.getElementById("form");
const error = document.getElementById("error");
const username = document.getElementById("username");
const password = document.getElementById("password");
const confirmRow = document.getElementById("confirm-row");
const confirmInput = document.getElementById("confirm");
let setup = false;

// Solange kein Konto existiert, legt die erste Anmeldung das Adminkonto an.
fetch("api/auth-state", { cache: "no-store" }).then(r => r.ok ? r.json() : null).then(state => {
  if (!state || state.passwordSet) return;
  setup = true;
  document.getElementById("hint").hidden = false;
  document.getElementById("password-label").firstChild.textContent = "Neues Passwort ";
  document.getElementById("submit").textContent = "Konto anlegen";
  username.value = "admin";
  password.autocomplete = "new-password";
  password.minLength = state.minLength;
  confirmRow.hidden = false;
  confirmInput.required = true;
}).catch(() => {});

function fail(text) {
  error.textContent = text;
  form.classList.remove("shake");
  void form.offsetWidth;
  form.classList.add("shake");
}

form.addEventListener("submit", async e => {
  e.preventDefault();
  error.textContent = "";
  if (setup && password.value !== confirmInput.value) {
    fail("Die beiden Eingaben stimmen nicht überein.");
    return;
  }
  try {
    const response = await fetch(setup ? "api/setup-password" : "api/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username: username.value, password: password.value }),
    });
    if (response.ok) {
      location.href = "./";
      return;
    }
    fail((await response.json()).error ?? "Die Anmeldung ist fehlgeschlagen.");
  } catch {
    fail("Der Server ist nicht erreichbar.");
  }
});
