# ShoppyAI — profil Darija DZ

ShoppyAI comprend mieux la darija algérienne (écriture arabe et arabizi) grâce au **prompting** et à la **gestion du contexte**. Les poids du modèle ne sont pas modifiés : ShoppyAI utilise toujours le modèle configuré (`qwen2.5-coder:14b` par défaut).

## Fonctionnement

À chaque message, `ShoppyAI.Core` :

1. détecte la langue du message : darija en lettres arabes ou latines (chiffres 3/7/9…), français, anglais ou arabe standard, avec les mots typiques d'autres variétés (marocain, tunisien…). La détection est heuristique et présentée au modèle comme une supposition ;
2. construit le message `system` envoyé à Ollama (`/api/chat`) :
   - une partie fixe : profil linguistique et exemples de dialogue (identique à chaque tour, ce qui permet le cache d'Ollama) ;
   - une partie du tour : langue de réponse, entrées du glossaire présentes dans la conversation, corrections de l'utilisateur ;
3. ajoute l'historique (rôles `user` et `assistant` dans l'ordre), en retirant les plus anciens messages si le contexte (`num_ctx`) est plein.

La conversation reste **en mémoire uniquement**. Rien n'est écrit sur disque, à part les réglages et les corrections.

## Fichiers linguistiques (`ShoppyAI.Core/Language/Data/`)

| Fichier | Rôle |
|---|---|
| `system-prompt.md` | profil permanent (règles de réponse, honnêteté sur les expressions ambiguës) |
| `darija-dz.glossary.json` | glossaire DZ/arabizi : `term`, `forms` (variantes latines et arabes), `meaning`, `note`, `dialect` |
| `dialogue-examples.json` | exemples de dialogues montrés au modèle |
| `dialect-markers.json` | mots repères pour la détection de langue |

Ces fichiers sont intégrés à la DLL : il faut recompiler après modification.

Pour enrichir le glossaire **sans recompiler**, créez `%LOCALAPPDATA%\ShoppyAI\language\glossary.user.json` avec le même format. Une entrée ayant le même `term` qu'une entrée par défaut la remplace.

```json
{ "entries": [ { "term": "barbech", "forms": ["barbech", "barbecht", "برباش"], "meaning": "fouiller, chercher partout" } ] }
```

## Langue de réponse

- **Auto** (par défaut) : même langue et même écriture que l'utilisateur. Un message court ou ambigu (« ok », « w la base de données ? ») garde la langue de la conversation.
- **Langue imposée** : Darija latin, Darija arabe, français, anglais ou arabe standard.

Le choix se fait dans la barre latérale (*Reply language*) ou avec `/lang auto|darija|darija-ar|fr|en|ar`. Il est enregistré dans `%LOCALAPPDATA%\ShoppyAI\settings.json`.

## Corrections linguistiques

Une correction indique au modèle le sens confirmé d'un mot. Elle passe avant le glossaire.

- **Fenêtre** : *Language corrections* dans la barre latérale. On peut ajouter, modifier, décocher pour désactiver sans supprimer, supprimer et annuler.
- **Commandes** (application et console) :
  - `/correct doka = maintenant | note`
  - `/corrections`
  - `/disable doka`, `/enable doka`
  - `/forget doka`
  - `/undo` (annule le dernier changement)

Les corrections et leur historique d'annulation sont stockés dans `%LOCALAPPDATA%\ShoppyAI\language\corrections.json`. Si ce fichier devient illisible, il est conservé en copie `.unreadable-*` et n'est pas écrasé.

## Réglages Ollama (`settings.json`)

`ollama.baseUrl`, `ollama.model`, `ollama.temperature` (0.5), `ollama.contextLength` (8192), `ollama.reservedAnswerTokens`, `ollama.maxAnswerTokens`, `ollama.seed`.

Les variables d'environnement `SHOPPYAI_OLLAMA_URL` et `SHOPPYAI_MODEL` les remplacent sans modifier le fichier. `SHOPPYAI_DATA_DIR` change le dossier de données.

## Évaluation avec le vrai modèle

`ShoppyAI.DarijaEval/darija-eval-dataset.json` contient 70 cas : 20 en darija arabe, 20 en arabizi, 10 mixtes, 10 conversations avec contexte ou corrections, 10 expressions ambiguës ou dialectales.

```powershell
ollama list                                   # qwen2.5-coder:14b doit être présent
dotnet test ShoppyAI.Tests
dotnet run --project ShoppyAI.DarijaEval -- --out eval-results\run1
# options : --only az-03,cv-04  --category arabizi  --model <nom>  --url <adresse>  --dry-run --show-prompt
```

Le rapport `eval-results\run1\report.md` contient toutes les réponses et des contrôles automatiques :

- écriture arabe ou latine ;
- langue détectée dans la réponse ;
- absence de formes marocaines ou tunisiennes ;
- question de clarification ;
- mots-clés attendus.

Ces contrôles sont des indices pour savoir quoi relire en premier. Ce ne sont **pas** un score de compréhension : chaque réponse doit être lue.
