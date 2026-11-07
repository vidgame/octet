# Octet

Octet est une application Windows qui analyse un lecteur ou un dossier et représente la taille de ses fichiers. L’analyse se fait sur le PC, sans envoyer de données.

## Explorer

1. Ouvrez `bin\portable\Octet.exe`, puis sélectionnez un lecteur ou cliquez sur **Choisir un dossier**.
2. Dans la **Carte**, chaque rectangle occupe une surface proportionnelle à la taille de l’élément. Dans l’**Arbre**, cliquez sur **+** pour déplier un dossier sur place. Les dossiers très fournis se parcourent par pages directement sur leur branche. Double-cliquez sur un dossier pour en faire le point de départ de l’arbre. **Liste** présente les mêmes éléments en lignes. **Gros fichiers** classe les fichiers du dossier et de ses sous-dossiers.
3. Cliquez sur un élément pour afficher son chemin complet dans la barre du bas. Utilisez **Copier** ou **Dans l’Explorateur** pour retrouver l’élément et le gérer depuis Windows. Après une modification sur le disque, cliquez sur **Réanalyser**.

Le bouton **Retour** et **Alt + Gauche** reviennent au dossier consulté précédemment. **Alt + Haut** ouvre le dossier parent. **Ctrl + F** recherche un nom dans le dossier courant et ses sous-dossiers, **Ctrl + L** sélectionne le chemin complet et **Ctrl + O** ouvre le sélecteur de dossier. Le classement et la recherche affichent jusqu’à 250 résultats, triés par taille.

Dans l’**Arbre**, les enfants d’un dossier sont empilés à sa droite. La molette zoome et dézoome sous le pointeur. Vous pouvez aussi utiliser les boutons **−** et **+** en haut de la vue. Glissez le fond pour déplacer la vue, ou maintenez le bouton du milieu pour la déplacer depuis un nœud. **Réinitialiser la vue** revient au début de l’arbre à la taille d’origine.

Les valeurs sont des tailles logiques en unités décimales. Les fichiers compressés, creux ou liés en dur peuvent expliquer un écart avec l’espace occupé sur le lecteur. La barre d’état indique les dossiers illisibles et les éléments redirigés ignorés, notamment les jonctions.

## Compiler

L’application utilise WPF et .NET 8. Le SDK .NET est nécessaire pour compiler. L’exécutable x64 a besoin du runtime de bureau .NET 8, déjà présent sur le PC de développement.

Ouvrez PowerShell dans le dossier `octet`.

```powershell
dotnet run --project .\Octet.csproj
dotnet publish .\Octet.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:EnableSingleFileAnalyzer=false -p:DebugType=none -o .\bin\portable
```
