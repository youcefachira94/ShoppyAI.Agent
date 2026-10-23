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

## Langue de réponse (conversation multilingue, V1.1)

Le mode **Auto** est le mode principal : l'utilisateur n'a rien à sélectionner. La langue du message ne s'impose pas automatiquement à la réponse : ShoppyAI garde la langue de la conversation et les préférences exprimées.

Pour chaque message, `ShoppyAI.Core` (`ConversationLanguage`) distingue :

- **la langue détectée** du message (simple indice) ;
- **la langue de réponse préférée** et **l'alphabet préféré** pour la darija (arabe ou latin), qui sont deux réglages séparés ;
- **les consignes explicites** trouvées dans le message (`LanguageInstructionParser`, indices dans `Language/Data/reply-cues.json`).

Ordre de priorité :

1. **Consigne pour cette réponse seulement** : « juste cette fois en anglais ».
2. **Consigne pour la conversation** : « réponds en français », « jawbni b darija », « machi b lfusha, b darija », « plus court stp ».
3. **Préférence enregistrée** : demandée avec « من اليوم », « désormais », « toujours », « from now on », « dima »…, ou choisie dans la barre latérale ou avec `/lang`.
4. **Langue de la conversation** :
   - un message isolé dans une autre langue (une question technique en anglais) ne la change pas ;
   - un message court ou flou (« oui », « vasy », « saha », « ok ») non plus ;
   - deux messages clairs d'affilée dans une autre langue la changent : l'utilisateur peut toujours changer de langue naturellement.
5. **Début de conversation** : la langue du premier message.

Quelques précisions :

- « بالعربية » / « en arabe » donne de la darija en lettres arabes ; l'arabe standard n'est utilisé que si l'utilisateur dit « الفصحى » ou « fusha ».
- « بالحروف العربية » / « ekteb b arabizi » change l'alphabet, et la langue reste la darija.
- « réponds dans ma langue » revient au mode Auto.
- Une demande de traduction (« traduis… », « comment on dit… en anglais ») n'est pas une consigne de langue.
- Si le parseur rate une demande, le modèle a pour consigne de suivre une demande explicite du dernier message.
- Le code, les commandes et les identifiants gardent leur écriture d'origine.

**Réglages et commandes** (application et console) :

- **Barre latérale :** *Reply language* (Auto, Darija, Français, English, العربية الفصحى), *Darija alphabet* (Auto, العربية, Latin), *Remember for new chats*, *Reset preferences*. La ligne « This chat » affiche les consignes données dans la conversation en cours.
- **Préférences générales :**
  - `/prefs` (afficher) et `/prefs reset` (tout remettre en Auto) ;
  - `/remember on|off`.
- **Langue, alphabet, longueur :**
  - `/lang auto|darija|darija-ar|darija-latin|fr|en|ar` ;
  - `/alphabet auto|arabic|latin` ;
  - `/length auto|short|detailed`.
- **Notes de style :** `/style <note>`, `/style`, `/style clear`.

Les préférences sont enregistrées dans `%LOCALAPPDATA%\ShoppyAI\settings.json` (section `preferences`) quand *Remember* est activé. Sinon, elles durent jusqu'à la fermeture de ShoppyAI. L'ancien réglage V1 `responseLanguage` est migré automatiquement. La console et l'application relisent le fichier quand l'autre l'a modifié.

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

`ShoppyAI.DarijaEval/darija-eval-dataset.json` contient 80 cas (115 tours) : 20 en darija arabe, 20 en arabizi, 10 mixtes, 10 conversations avec contexte ou corrections, 10 expressions ambiguës ou dialectales, et 10 cas « preferences » (changements de langue, préférences durables, messages courts, corrections). Le cas pf-01 est le scénario de référence : « wach rak ? », puis « جاوبني بالحروف العربية من الآن », puis « what can you do for me? ».

Le `--dry-run` remplace le modèle par une réponse fixe. Il vérifie donc sans GPU la langue de réponse demandée à chaque tour (contrôles `reply-language` et `answer-length`).

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
