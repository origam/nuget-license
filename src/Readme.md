## Semi Automatic Attributions Generation
This file describes semi automatic attributions generation for Origam. Check OrigamAttributionsGenerator for more automated approach

### Server Attributions 
first run the NugetUtility with:

`-i "origam\backend\Origam.sln" --outfile "origam\frontend-html\public\Attibutions.txt" --git-hub-auth-token ghp_XXXXXXXXXXXXXXXXXXX`

then to add the javascript attributions run this command in the "origam\frontend-html" folder:

`yarn licenses generate-disclaimer > public/Attributions_js.txt`

open the file. Remove any extra out put which might have been inserted at the beginning or the end of the file. And copy the rest to the `Attibutions.txt` file.

### Architect Attributions
copy the file `origam\frontend-html\public\Attibutions.txt` to `origam\backend\OrigamArchitect\Attributions.txt`


