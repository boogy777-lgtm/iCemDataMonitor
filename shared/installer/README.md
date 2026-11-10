# Shared installer build inputs

This first layout increment gives the common WiX tool pin and MSI database reader
one owner. `WindowsMsiDatabase.psm1` is used by the shared OPC Runtime builder;
both application-family MSI/Setup fixture consumers restore the WiX tool from the
single `.config/dotnet-tools.json` here. Product authoring and family identity
remain in their respective product trees.

The existing Setup launcher source and its build entry point remain at their
current paths for this stage: moving their consumer requires changing the
baseline-approved legacy release workflow, and this increment does not rebind
that approval. No wrapper or duplicate launcher was introduced.
