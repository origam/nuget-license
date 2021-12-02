## Architect Attributions
run the NugetUtility with:

`-i "origam\backend\OrigamArchitect\OrigamArchitect.csproj" --outfile "origam\backend\OrigamArchitect\Attributions.txt" --git-hub-auth-token ghp_XXXXXXXXXXXXXXXXXXX`

## Server Attributions 
first run the NugetUtility with:

`-i "origam\backend\Origam.ServerCore\Origam.ServerCore.csproj" --outfile "origam\frontend-html\public\Attibutions.txt" --git-hub-auth-token ghp_XXXXXXXXXXXXXXXXXXX`

then to add the javascript attributions run this command in the "origam\frontend-html" folder:

`yarn licenses generate-disclaimer > public/Attributions_js.txt`

open the file. Remove any extra out put which might have been inserted at the beginning or the end of the file. And copy the rest to the `Attibutions.txt` file.

