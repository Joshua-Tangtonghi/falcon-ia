Utilisation complète des outils Q-Learning (guide pour les collègues)

But du document
Ce README explique comment :
- configurer et lancer l'entraînement d'un agent Q‑Learning dans ce projet Unity,
- tweaker les hyperparamètres et la discretisation,
- sauvegarder / charger / importer / fusionner des Q‑tables (JSON) partagées par des collègues,
- comparer et évaluer plusieurs agents.

Prérequis
- Ouvre le projet dans Unity Editor et attends la fin de la compilation.
- Ce guide suppose que les scripts fournis dans `Assets/AI/` sont présents :
  - `QLearningAgent.cs`, `QLearningController.cs`, `QLearningTrainer.cs`,
  - utilitaires d'éditeur : `Assets/AI/Editor/QLearningSetup.cs`, `QTableUtilsEditor.cs`.

Vue d'ensemble des composants
- `QLearningAgent` : gestion de la table Q (save/load JSON). Maintenant inclut un wrapper `metadata` dans le JSON (auteur, actionCount, hyperparams, encodingVersion, date).
- `QLearningController` : convertit l'état du jeu en clé discrète, mappe 9 actions (3 thrust × 3 steer) vers `InputData` et appelle l'agent.
- `QLearningTrainer` : helper en scène pour entraîner, logger les épisodes, sauvegarder/charger et accélérer le training (Time.timeScale).
- `QTableUtilsEditor` : menu d'éditeur pour importer une Q‑table depuis le disque et pour fusionner deux Q‑tables.
- `QLearningSetup` : menu d'éditeur pour créer un prefab et un trainer dans la scène automatiquement.

Créer rapidement un prefab + trainer
1. Dans Unity Editor : Tools → QLearning → Create Example Prefab & Trainer
   - Crée `Assets/AI/QLearningController.prefab`
   - Instancie `QLearningController_Instance` en scène et crée `QLearningTrainer` lié au controller.
2. Sélectionne `QLearningTrainer` dans la hiérarchie et règle les options dans l'Inspector.

Champs importants dans `QLearningTrainer` (Inspector)
- `controller` : référence au `QLearningController` (souvent auto-assigned par le menu Create).
- `SaveFileName` : nom du fichier JSON utilisé pour save/load (ex: `qtable.json`).
- `AutoLoad` : charger la Q‑table automatiquement au démarrage si le fichier existe.
- `AutoSaveOnEnd` : sauvegarder à la fin de chaque épisode.
- `AutoSaveEveryNEpisodes` : sauvegarde automatique tous les N épisodes (0 = désactivé).
- `MovingAverageWindow` : fenêtre utilisée pour la moyenne mobile affichée dans les logs.
- `TrainingTimeScale` : multiplie `Time.timeScale` pendant l'entraînement (mettre >1 pour accélérer).
- `PersistAcrossEpisodes` : si true, le trainer persiste entre reloads de scène pour accumuler stats.

Champs importants dans `QLearningController` (Inspector)

Voici la liste des champs exposés dans `QLearningController` et l'effet concret de les modifier. Ces paramètres sont réglables en temps réel dans l'Inspector pendant Play Mode.

- Contrôles d'apprentissage
  - `Alpha` (learning rate) : contrôle la rapidité d'actualisation des Q-values.
    - Effet : valeurs élevées (par ex. >0.5) rendent l'agent très réactif aux nouvelles expériences mais peuvent provoquer de l'instabilité ; valeurs basses (<0.2) ralentissent l'apprentissage.
    - Conseil : 0.3–0.6 par défaut.
  - `Gamma` (discount factor) : combien l'agent prend en compte les récompenses futures.
    - Effet : proche de 1 favorise la considération de récompenses à long terme; proche de 0 privilégie les récompenses immédiates.
    - Conseil : 0.9–0.99.
  - `Epsilon` : probabilité d'explorer (choisir une action aléatoire).
    - Effet : plus élevé → exploration accrue (utile au début).
    - Conseil : commencer 0.2–0.5, puis laisser `EpsilonDecay` réduire progressivement.
  - `EpsilonDecay` : facteur multiplicatif appliqué à `Epsilon` après chaque appel à `Learn`.
    - Effet : contrôle la vitesse de passage de l'exploration à l'exploitation.
  - `MinEpsilon` : epsilon minimal (n'apparaîtra pas en dessous).

- Action space et discretisation
  - `thrustLevels` : vecteur de niveaux de poussée discrets (ex. [0, 0.5, 1]).
    - Effet : augmenter le nombre de niveaux donne des actions plus fines mais augmente linéairement le nombre total d'actions.
  - `steerAngles` : valeurs d'angles appliquées au cap vers la cible (ex. [-30, 0, 30]).
    - Effet : plus d'angles = manoeuvres plus granulaires.
  - `nearFactor`, `midFactor` : facteurs utilisés pour bucketiser la distance au waypoint (définissent seuils near/mid/far).
  - `avoidAheadDistance`, `avoidConeAngle` : paramètres d'évitement d'astéroïdes (contrôlent quand la manœuvre d'évitement est déclenchée).

- Nouveaux champs "Rewards & penalties" exposés dans l'Inspector
  - `rewardPerWaypoint` : récompense attribuée lorsqu'un waypoint est capturé (par waypoint). Augmenter accélère l'orientation de l'agent vers la capture de waypoints.
  - `rewardPerScore` : récompense appliquée quand le score augmente (par ex. hit réussi sur l'ennemi).
  - `livingPenalty` : petite pénalité appliquée chaque step pour encourager à atteindre la cible rapidement (peut être négative ou 0).
  - `rewardForShot` / `rewardForDropMine` / `rewardForShockwave` : bonus d'exploration ajoutés si l'action précédente déclenchait respectivement un tir, une mine ou une shockwave.
    - Utilité : encourage l'expérimentation des armes durant la phase d'exploration.
  - `penaltyOnHit` : pénalité appliquée quand le vaisseau subit un hit (valeur négative pour punition).

  - Comment ces champs sont utilisés : à la fin de chaque step d'entraînement la récompense envoyée à `QLearningAgent.Learn` est calculée comme une somme pondérée :
    reward = waypoints_captured * rewardPerWaypoint + score_increase * rewardPerScore + livingPenalty + explorationBonuses + hitPenalties
    (les `explorationBonuses` proviennent de `rewardForShot`/`rewardForDropMine`/`rewardForShockwave` si l'action précédente a déclenché ces armes ; `hitPenalties` = hitDiff * penaltyOnHit)

  - Conseils pratiques :
    - Si les armes sont rarement testées, augmente temporairement `rewardForShot`/`rewardForDropMine`/`rewardForShockwave` pour encourager l'agent à essayer ces actions.
    - Si le vaisseau subit trop de dégâts, rendez `penaltyOnHit` plus négatif pour décourager comportements dangereux.


Exemples de tuning rapides
  - Phase d'exploration (forcer essais armes) : `Epsilon=0.4`, `rewardForShot=0.8`, `rewardForDropMine=0.8`, `rewardForShockwave=1.0`, `livingPenalty=-0.01`.
  - Phase d'exploitation (stabiliser comportement) : `Epsilon=0.05`, `EpsilonDecay` proche de 1.0 mais `MinEpsilon` faible.
  - Punitions : si l'agent subit trop de hits, `penaltyOnHit = -1.0` ou inférieur selon criticité.



Utilisation pratique depuis l'Inspector
  1. Sélectionne le GameObject qui contient `QLearningController` (ou le prefab) dans la scène.
  2. Dans l'Inspector, sous "Agent parameters" règle `Alpha`, `Gamma`, `Epsilon`, puis `EpsilonDecay`.
  3. Sous "Rewards & penalties" règle `rewardPerWaypoint`, `rewardPerScore`, `livingPenalty`, `rewardForShot`, etc.
  4. Active `DebugActions` pour voir des logs périodiques d'utilisation d'armes et des messages quand une action d'arme est tentée mais bloquée par manque d'énergie.
  5. Lance Play Mode, observe les logs et ajuste les valeurs en direct.

Procédure complète d'entraînement (step-by-step)
1. Préparer la scène de test (utiliser le prefab créé) et assigner `QLearningController` au joueur.
2. Ouvrir `QLearningTrainer` et régler : `SaveFileName`, `TrainingMode = true`, `TrainingTimeScale` (ex: 2.0 pour accélérer), `AutoSaveEveryNEpisodes` si souhaité.
3. Régler hyperparamètres dans `QLearningController` (Alpha, Gamma, Epsilon, etc.).
4. Lancer Play Mode.
   - Touches utiles (QLearningTrainer) :
     - K : save la qtable (affiche le chemin)
     - L : load la qtable
     - P : toggle TrainingMode (apprentissage on/off)
     - R : restart scene
5. Laisser tourner plusieurs épisodes. Le trainer affiche à la fin de chaque épisode :
   Episode N: score=S, waypoints=W, avgScore(M)=X.XX, avgWaypoints(M)=Y.YY
6. Sauvegarder régulièrement (K) ou activer `AutoSaveEveryNEpisodes`.

Comment évaluer et comparer deux agents
1. Charger agent A (SaveFileName -> qtable_A.json), mettre `TrainingMode=false`.
2. Lancer N épisodes (ex: 20) et noter score moyen et waypoints moyen (le trainer loggue déjà les stats).
3. Répéter pour agent B.
4. Comparer moyennes et variance. Plus élevé = meilleur; moins de variance = plus robuste.

Importer un agent d'un collègue (procédure sûre)
1. Le collègue t'envoie `their_qtable.json` (idéalement avec metadata). Ne remplace pas directement ton `qtable.json`.
2. Menu : Tools → QLearning → Import QTable... → sélectionne `their_qtable.json` et sauve le fichier dans `Application.persistentDataPath` (par ex `qtable_their.json`).
   - L'outil vérifie l'`actionCount` dans le metadata par rapport au controller présent et t'avertit si différent.
3. Dans l'inspector du `QLearningTrainer` : change `SaveFileName` à `qtable_their.json` et en Play Mode appuie sur L pour charger.
4. Tester en `TrainingMode=false` plusieurs épisodes pour évaluer.

Remplacer (overwrite) ta qtable locale
- Sauvegarde d'abord ton qtable local (backup). Ensuite copie/écrase le fichier `qtable.json` dans `Application.persistentDataPath` et appuie sur L pour charger.
- Toujours faire un backup avant overwrite.

Fusionner deux Q-tables (outil automatique)
- Menu : Tools → QLearning → Merge QTables...
- Choisis d'abord la Q-table A (ex: la tienne), puis la Q-table B (ex: celle du collègue).
- L'outil :
  - vérifie `actionCount` et `encodingVersion` et demande confirmation si differents,
  - pour états communs effectue moyenne simple des Q-values,
  - pour états uniques conserve la Q-value telle quelle.
- Sauvegarde le fichier fusionné (choisis emplacement / nom) et teste la Q‑table fusionnée.

Conseils pour merger (bonnes pratiques)
- Favoriser la fusion pondérée si tu connais combien d'épisodes chaque agent a vu : Q_new = (n1*Q1 + n2*Q2) / (n1+n2). Metadata utile.
- Garder toujours la provenance dans `metadata.author` (ex: "alice;merged:bob").

Validation et compatibilité
Avant d'utiliser un qtable d'un collègue :
- Vérifier `metadata.actionCount` == `thrustLevels.Length * steerAngles.Length`.
- Vérifier `encodingVersion`. Si différent, demande le détail de l'encodage de l'état.
- Si incompatible, charger sous un nom séparé et tester, ou refuser l'import.


Commandes Windows utiles (cmd.exe)
- Copier un fichier reçu dans persistentDataPath (adaptation nécessaire) :

```bat
copy "C:\Users\Alice\Downloads\their_qtable.json" "%USERPROFILE%\\AppData\\LocalLow\\<Company>\\<Product>\\qtable_their.json"
```

Tester la Q-table importée (manuel rapide)
- Dans Unity, set `SaveFileName` = `qtable_their.json` et en Play Mode appuie sur L. Puis met `TrainingMode=false` et lance 20 épisodes pour obtenir la performance moyenne.

Exemples de workflows d'équipe
A. QA rapide d'un agent externe
- Import le fichier en `qtable_their.json` (sans écraser). Teste 20 épisodes en greedy. Si meilleur, discuter intégration.

B. Fusion par moyenne (safe)
- Merge les deux qtables via l'outil Merge, sauvegarde sous `qtable_merged.json`, teste en greedy, puis optionnellement remplace ta qtable locale.

C. Pipeline d'amélioration collaborative
- Chaque membre entraîne localement et génère `qtable_<name>_vN.json` avec metadata incluant nombre d'épisodes.
- Un responsable merge périodiquement (moyenne pondérée selon nombre d'épisodes) et publie la qtable partagée.

Bonnes pratiques et recommandations de tuning
- Alpha : 0.1–0.7. Trop grand => instable; trop petit => lent.
- Gamma : 0.9–0.99. Plus haut favorise récompenses futures.
- Epsilon : commencer autour de 0.2–0.4 puis laisser `EpsilonDecay` diminuer.
- MinEpsilon : 0.01 pour garder un faible hasard final.
- augmenter `TrainingTimeScale` pour accélérer l'apprentissage dans l'Editor (attention comportement physique si très élevé).
- Ajouter une récompense de shaping (ex: réduction de la distance vers la cible) si l'apprentissage est trop lent.

Dépannage commun
- "Le JSON ne se charge pas" : vérifier chemin (Application.persistentDataPath), que le fichier est valide JSON, et que `SaveFileName` est correct.
- "Actions étranges après import" : vérifier `actionCount` et mapping (thrustLevels × steerAngles).
- "Pas d'amélioration" : augmenter shaping reward, vérifier que l'agent reçoit des reward positifs quand il capture des waypoints.
