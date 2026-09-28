# Revalidación de corridas

## Despliegue

1. Aplicar `sql/007_runs_datos_hash.sql` a cada base existente. En una base nueva, `sql/DBFull.sql` ya trae la columna.
2. Publicar la API incluyendo `SimulationData/3/`: los ocho JSON de `Client/Data/` y todos los `*.json` de `Client/Data/layouts/`. El proyecto los copia a la salida. `GameData/` contiene economía del servidor y queda fuera del hash.
3. Confirmar que `DataVersion` sea 3. Si se configura `DataHash`, la API exige que coincida con el hash calculado del snapshot. `/run/start` siempre exige el hash real, incluso si `DataHash` está vacío.

Para una temporada nueva, copiar el conjunto completo a `SimulationData/<version>/` sin modificar los directorios históricos, actualizar `DataVersion` y publicar el paquete de simulación correspondiente. El hash se calcula concatenando los textos crudos en este orden: enemigos, clases, perks, planos, generador-armas, curvas, combate, sinergias, luego los layouts válidos `*.json` ordenados por nombre ordinal. Se reemplaza CRLF por LF en el conjunto y se aplica SHA-256 UTF-8 en hexadecimal minúscula, como `Client/Scripts/DataLoader.cs`.

## Ejecución controlada

En un proceso aparte, con la misma configuración de base y archivos que el servidor:

```sh
dotnet TowerOfArgentum.Api.dll --revalidate-pending 20
```

Acepta entre 1 y 100 corridas por invocación. No abre un endpoint HTTP para revalidar. Guarda `validado=1` cuando coincide, `2` cuando la simulación difiere y `3` cuando el log es ilegible. Una diferencia necesita revisión humana: no demuestra trampa. Las corridas previas a la migración, sin `datos_hash`, permanecen pendientes y el lote no las procesa; tampoco procesa hashes cuyo snapshot ya no esté instalado.

El comando usa el mismo `SchemaCheck` del servidor y se detiene si falta la migración. Cada despliegue debe conservar todos los snapshots históricos que quiera seguir revalidando.
