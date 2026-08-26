-- =============================================================================
--  000 — La base y el usuario
--
--  Va antes que todas las migraciones porque crea el terreno donde corren. Se
--  ejecuta UNA vez por instalación, conectado como root:
--
--      mysql -h 127.0.0.1 -P 46000 -u root -p < 000_base_y_usuario.sql
--
--  ⚠ EN LOCAL EL USUARIO VA SIN CONTRASEÑA, A PROPÓSITO. En producción se le
--    pone una y se corre el ALTER USER que está al final de este archivo.
--
--    Por qué es tolerable acá y no allá: el usuario está atado a
--    'towerdef'@'127.0.0.1', y MySQL filtra por host ANTES de mirar la
--    credencial. Aunque el servidor escuche en 0.0.0.0:46000 —que es como está
--    hoy en esta máquina—, una conexión desde la red no llega ni a intentar
--    autenticarse con este usuario: la rechaza por host.
--
--    Lo que sí puede: cualquier proceso que corra EN ESTA MÁQUINA se conecta
--    sin credencial. En una máquina de desarrollo es un riesgo aceptado; en un
--    servidor expuesto no lo es, y por eso el ALTER USER del final no es
--    opcional cuando esto salga de acá.
--
--  Por qué utf8mb4 y no latin1:
--
--    La base `juegob` de Austral es latin1, pero eso es herencia del VB6, no una
--    decisión. Este proyecto es nuevo y no arrastra ese lastre: va todo en
--    español con acentos y eñes (CLAUDE.md, regla de oro 4), y latin1 en MySQL
--    ni siquiera es Latin-1 de verdad — es cp1252 y no entra un emoji.
--
--    ⚠ La migración 004 del LoginServer —la que pasó sus tablas a latin1— NO se
--      copia. Es de las pocas cosas de allá que acá serían un error.
--
--  Por qué la colación 0900_ai_ci:
--
--    Es insensible a mayúsculas Y a acentos. O sea que "Nacho", "nacho" y
--    "Náchо" son el mismo nick a la hora del UNIQUE. En un juego con ranking
--    público eso importa: sin insensibilidad a acentos, registrar "Nаcho" con
--    tilde es suplantar a otro jugador en la tabla, y el UNIQUE deja pasar.
-- =============================================================================


-- -----------------------------------------------------------------------------
--  La base
-- -----------------------------------------------------------------------------
-- IF NOT EXISTS para que el archivo se pueda correr dos veces sin romper nada:
-- no hay tabla de control de migraciones, así que la garantía de este proyecto
-- es que cada archivo sea idempotente (CLAUDE.md).

CREATE DATABASE IF NOT EXISTS `towerdef`
    CHARACTER SET utf8mb4
    COLLATE utf8mb4_0900_ai_ci;


-- -----------------------------------------------------------------------------
--  El usuario de la API
-- -----------------------------------------------------------------------------
-- Usuario propio, NO root. La API se conecta con este y solo puede tocar
-- `towerdef`: si algún día hay una inyección, el radio de daño es esta base y no
-- la instancia entera —donde también vive `juegob`, la base del MMO—.
--
-- Solo desde 127.0.0.1: la API corre en la misma máquina que MySQL. Un usuario
-- con '%' aceptaría conexiones desde cualquier lado de la red.

-- Sin IDENTIFIED BY: contraseña vacía, que es lo que se decidió para local.
-- Ver el aviso del encabezado y el ALTER USER del final.

CREATE USER IF NOT EXISTS 'towerdef'@'127.0.0.1';

-- Los permisos van sobre `towerdef`.* y nada más. Sin GRANT OPTION, sin FILE,
-- sin PROCESS: la API inserta, lee y actualiza filas, no administra el servidor.
--
-- Sin DROP a propósito: las migraciones las corre una persona, con su propio
-- usuario. Que el proceso que atiende internet no pueda borrar una tabla ni por
-- accidente ni por inyección es gratis y vale la pena.

GRANT SELECT, INSERT, UPDATE, DELETE
    ON `towerdef`.*
    TO 'towerdef'@'127.0.0.1';

FLUSH PRIVILEGES;


-- -----------------------------------------------------------------------------
--  Verificación
-- -----------------------------------------------------------------------------
-- Que la base quedó con el charset y la colación correctos. Si esto devuelve
-- latin1 es porque la base YA existía de antes con otro charset: CREATE DATABASE
-- IF NOT EXISTS no la corrige, la deja como está y no dice nada.

SELECT
    SCHEMA_NAME                 AS base,
    DEFAULT_CHARACTER_SET_NAME  AS charset,
    DEFAULT_COLLATION_NAME      AS colacion
FROM information_schema.SCHEMATA
WHERE SCHEMA_NAME = 'towerdef';
