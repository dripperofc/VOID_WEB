# VOID — Web Client

> Cliente web do VOID — acessa o chat em qualquer dispositivo sem instalar nada.

**[→ Abrir VOID Web](https://void-web.onrender.com)**

---

## O que é

VOID é um chat em tempo real com voz, DMs, canais e servidores — inspirado no Discord, feito do zero. Este repositório é o cliente web: HTML/CSS/JS puro.

## Como usar

Abre o link acima, cria conta ou usa as credenciais do VOID Desktop. Tudo sincroniza no mesmo servidor.

## Como testar localmente

```bash
python3 -m http.server 8080
# Abre http://localhost:8080
```

---

## Stack

- HTML + CSS + JS puro — sem framework, sem build
- SignalR para mensagens em tempo real
- WebRTC para chamadas de voz P2P

---

## Repositórios

| Repo | Descrição |
|------|-----------|
| [VOID](https://github.com/dripperofc/VOID) | Cliente desktop (Avalonia / .NET) |

---

## Status

`PRE-ALPHA` — em desenvolvimento ativo. Bugs são esperados.

---

*VOID Project © 2026 — Licença MIT*
