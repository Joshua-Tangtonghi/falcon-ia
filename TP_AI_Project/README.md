# Q-Learning – Explication simple

Ce projet fait apprendre à un vaisseau à se comporter tout seul dans le jeu. Il observe la situation, essaye des actions, reçoit des points (ou des pénalités), et ajuste petit à petit une "table" interne de ce qui marche bien.

## Idée générale
On a des situations (états) et des actions possibles. Le vaisseau garde un score estimé: "Si je fais telle action dans ce contexte, combien ça va payer ?". Ces scores sont stockés dans une table (Q-table).

## Comment il choisit
Deux options:
- Explorer (au hasard) pour découvrir (probabilité ε).
- Exploiter: prendre l’action avec le meilleur score connu.

Formule d’exploration: si random < ε alors action aléatoire, sinon meilleure action.

## Comment il apprend
Après chaque action, il reçoit une récompense r. Il regarde aussi l’état suivant pour estimer le futur.

Formule de mise à jour (simplifiée):
Q(nouvel_estimation) = Ancien + α × (Cible − Ancien)
Avec:
- Cible = r + γ × MeilleurScoreÉtatSuivant (si ce n’est pas fini)
- Cible = r (si la partie est finie)
- α: vitesse d’apprentissage
- γ: importance du futur

ε diminue au fil du temps (on explore moins quand on a appris):
ε = max(ε_min, ε × decay)

## Récompense (ce qui motive le vaisseau)
On additionne plusieurs éléments:
- Points waypoint si on en gagne
- Points de score général si hausse
- Points quand on touche un adversaire
- Pénalité quand on se fait toucher
- Bonus si on a utilisé une shockwave
- Bonus ou pénalité selon niveau d’énergie (trop bas ou trop haut)
- Petite pénalité/bonus de "vivre" chaque pas (living)

Total = somme de tout ça (seulement les éléments qui s’appliquent à ce pas de temps).

## Ce qu’il voit (état)
On transforme la situation en mots-clés:
- Distance / orientation vers le prochain point (near/mid/far + front/left/right)
- Menaces (balles, mines): proche/loin, va toucher ou pas
- Niveau d’énergie (soi / ennemi): low/mid/high
- Différence de score / hits / waypoints: ahead/close/behind
- Temps restant: early/mid/late
- Ennemi en ligne de vue: yes/no
- Un petit calcul de priorité d’objectif (heuristique) classé: low/mid/high
Tout est concatené en une seule chaîne: c’est la clé de la Q-table.

## Actions possibles
36 actions = combinaison de:
- 3 niveaux de poussée
- 3 variations de direction
- 4 choix "arme" (tir / mine / shockwave / rien)

## Cycle d’apprentissage
1. Encoder l’état (faire la chaîne de mots)
2. Choisir une action (explorer ou exploiter)
3. Appliquer l’action dans le jeu
4. Calculer la récompense
5. Mettre à jour la Q-table
6. À la fin: mise à jour finale avec la récompense terminale
7. Sauvegarder la table en JSON pour la réutiliser

## Sauvegarde
Un fichier JSON contient:
- Les paramètres (alpha, gamma, epsilon, etc.)
- Chaque état déjà vu et la liste des scores pour ses actions

## Paramètres principaux
- α (alpha): vitesse pour corriger les estimations
- γ (gamma): poids du futur
- ε (epsilon): fréquence d’essais aléatoires
- ε_decay: vitesse de baisse de ε
- ε_min: valeur plancher de ε

## Formules mathématiques
Politique ε-greedy:
P(exploration) = ε ; P(exploitation) = 1 − ε
Action exploit: a* = argmax_a Q(s, a)

Mise à jour Q:
Si non terminal: y = r + γ · max_{a'} Q(s', a')
Si terminal: y = r
Q(s, a) ← Q(s, a) + α · ( y − Q(s, a) )

Décroissance de l’exploration:
ε ← max(ε_min, ε · decay)

Décomposition de la récompense instantanée:
r = k_wp·ΔWP + k_sc·ΔScore + k_hit·ΔHitScore + k_gotHit·ΔGotHit + k_sw·I_shockwave + r_living + r_énergie
Avec I_shockwave = 1 si la shockwave a été utilisée au pas précédent, sinon 0.

Pénalité / bonus énergie (si activé):
Si e > thr_high: r_énergie_high = λ_high · (e − thr_high)/(1 − thr_high)
Si e < thr_low:  r_énergie_low  = λ_low  · (thr_low − e)/thr_low
Sinon r_énergie = 0.

Heuristique objectif (priorité du checkpoint):
h = 1/(1 + d_cp) + b_owner
b_owner = −0.5 si cible alliée, 1 si ennemie.
Bucket: high si h ≥ 1, mid si 0 ≤ h < 1, low si h < 0.

Indexation action (thrustIdx, steerIdx, weaponIdx):
index = thrustIdx*(3*4) + steerIdx*4 + weaponIdx
thrustIdx ∈ {0,1,2}, steerIdx ∈ {0,1,2}, weaponIdx ∈ {0,1,2,3}

Exemple numérique:
Supposons Q(s,a)=2.0, r=1.0, max_{a'}Q(s',a')=3.0, γ=0.9, α=0.5.
Cible y = 1.0 + 0.9*3.0 = 3.7
Nouveau Q(s,a) = 2.0 + 0.5*(3.7 − 2.0) = 2.85

## Où regarder dans le code
- QLearningAgent.cs : choisir, apprendre, sauver/charger
- QLearningController.cs : encoder l’état, calculer récompenses, convertir action → commandes

## Pour résumer
Le vaisseau: observe → choisit → agit → reçoit des points → ajuste sa table. Avec le temps il choisit mieux sans qu’on lui donne des règles manuelles.

