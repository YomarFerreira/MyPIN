# MyPIN

Notas adesivas para Windows (WPF / .NET 10).

## Executar / compilar

```powershell
dotnet run --project src\MyPIN                                  # rodar em modo dev
dotnet publish src\MyPIN -c Release -o publish                  # gerar publish\MyPIN.exe
```

Os dados ficam em `%AppData%\MyPIN\notes.json` (com `notes.bak.json` de segurança, gerado a cada início).

**Gerar o instalador** (para distribuir): `.\tools\gerar_instalador.ps1` → `dist\MyPIN-Setup-<versão>.exe`.
Precisa do Inno Setup 6 (`winget install JRSoftware.InnoSetup`). O instalador leva o .NET junto, instala só
para o usuário (sem administrador) e mostra os termos de uso (`installer\termos.txt`).

**Modo de teste:** defina a variável `MYPIN_DATA` com outra pasta. O app usa essa pasta e roda
separado da instância normal, sem tocar nas notas de verdade.

## Recursos

- Notas soltas na área de trabalho: mover, redimensionar, 9 cores, transparência quando fora de foco
- Texto com formatação: negrito, itálico, sublinhado, tachado, marca-texto, listas, caixas de tarefa ☐/☑
- Zoom do texto por nota e "ajustar a altura ao texto"
- Localizar dentro da nota, imprimir, exportar (.txt / .rtf), duplicar
- Compartilhar: copiar texto, copiar com formatação, e-mail, WhatsApp
- Bloquear nota (trava texto, posição e tamanho)
- Cadernos com cor própria; mostrar/ocultar um caderno inteiro da tela (ícone da bandeja ou gerenciador)
- Lembretes com data/hora (som + aviso + a nota pula na tela)
- Gerenciador com cadernos, pesquisa (ignora acentos), backup/importação
- Lixeira acessível pelo ícone da bandeja, com prazo configurável (7 dias a 1 ano, ou nunca apagar)
- IA nas notas (botão ✨ / Ctrl+Shift+I): ações rápidas (resumir, corrigir, lista de tarefas, continuar,
  traduzir) e conversa livre; a resposta só entra na nota quando você clica. Provedores: Anthropic (Claude),
  OpenAI, Google Gemini, Ollama (local) ou qualquer API compatível com OpenAI. Configuração no ícone da
  bandeja → "Inteligência Artificial"; a chave fica em `ai.json`, criptografada pelo Windows (DPAPI) e fora dos backups.
- Menus no tema do Windows (claro/escuro), ícone na bandeja, iniciar com o Windows, salvamento automático

## Atalhos

| Atalho | Ação |
|---|---|
| Ctrl+Alt+N (global) | Nova nota |
| Ctrl+Alt+S (global) | Mostrar/trazer todas as notas para frente |
| Ctrl+N | Nova nota |
| Ctrl+W / Ctrl+Shift+W | Ocultar nota / ocultar as outras |
| Ctrl+M | Recolher / expandir |
| Ctrl+D | Duplicar |
| Ctrl+F | Localizar na nota |
| Ctrl+P / Ctrl+S | Imprimir / exportar |
| Ctrl+Shift+A | Lembrete |
| Ctrl+Shift+T | Sempre visível |
| Ctrl+Shift+B | Bloquear / desbloquear |
| Ctrl+Shift+Del | Excluir nota (vai para a lixeira) |
| F2 | Renomear (título) |
| Ctrl+Shift+I | Abrir/fechar o painel de IA |
| Ctrl++ / Ctrl+− / Ctrl+0 / Ctrl+roda do mouse | Zoom do texto |
| Ctrl+B / I / U / T | Negrito / itálico / sublinhado / tachado |
| Ctrl+H | Marca-texto |
| Ctrl+Shift+L | Lista com marcadores |
| Ctrl+Shift+C | Caixa de tarefa ☐ (clique nela para marcar) |
| Ctrl+Shift+V | Colar como texto simples |
| F5 | Inserir data e hora |

## Licença

Copyright © 2026 Yomar. Todos os direitos reservados. Uso gratuito; cópia, modificação e
redistribuição só com autorização do autor. Veja [LICENSE](LICENSE) e
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
