-- =============================================================================
--  001 — Cuentas
--
--  La tabla de jugadores. Login propio: este proyecto NO comparte cuentas con
--  Austral AO ni con su base `juegob`.
--
--      mysql -h 127.0.0.1 -P 46000 -u root -p towerdef < 001_cuentas.sql
--
--  Idempotente: se puede correr dos veces. No hay tabla de control de
--  migraciones, así que la garantía es que cada archivo se banque repetirse.
-- =============================================================================


CREATE TABLE IF NOT EXISTS `cuentas` (

    -- BIGINT y no INT: es gratis en una tabla que va a tener pocas filas, y
    -- quedarse sin ids es de los problemas más molestos de arreglar en caliente.
    `id`              BIGINT UNSIGNED  NOT NULL AUTO_INCREMENT,

    -- El nombre que se ve en el ranking.
    --
    -- ⚠ La colación de la base es utf8mb4_0900_ai_ci: insensible a mayúsculas Y
    --   a acentos. O sea que este UNIQUE hace que "Nacho", "nacho" y "Nácho"
    --   sean el mismo nick. En un juego con tabla pública eso importa: sin
    --   insensibilidad a acentos, registrarse con una tilde de diferencia es
    --   suplantar a otro jugador y el UNIQUE lo deja pasar.
    `nick`            VARCHAR(20)      NOT NULL,

    -- Cadena PHC completa de Argon2id, con los parámetros adentro:
    --   $argon2id$v=19$m=19456,t=2,p=1$<sal>$<hash>
    --
    -- Guardar los parámetros con el hash es lo que permite subir el costo más
    -- adelante sin invalidar nada: cada contraseña se verifica con los
    -- parámetros con los que se creó, y se re-hashea sola en el próximo login.
    -- 255 es holgado; hoy la cadena mide ~100.
    `password`        VARCHAR(255)     NOT NULL,

    `email`           VARCHAR(120)     NULL,

    -- -------------------------------------------------------------------------
    --  La billetera
    -- -------------------------------------------------------------------------
    -- ⚠ Esto NO es el oro de la corrida. El oro de la corrida muere con ella;
    --   esto es progresión permanente y sirve para comprar planos de fusión.
    --
    -- ⚠ El cliente NUNCA manda un valor para esta columna. /run/finish reporta
    --   hasta dónde llegó y el servidor deriva oroGanado = f(gano, oleada). Es
    --   la única forma: oro falso compra todos los planos y se vuelve poder real,
    --   y la columna `validado` de runs no sirve de freno porque el oro se gasta
    --   al instante.
    --
    -- UNSIGNED para que un bug de resta no deje a nadie con oro negativo, que en
    -- una comparación con signo pasa cualquier chequeo de "¿te alcanza?".
    `oro`             BIGINT UNSIGNED  NOT NULL DEFAULT 0,

    -- VARBINARY(16) entra un IPv6 completo, y un IPv4 mapeado ocupa lo mismo.
    -- Guardar la IP como texto costaría 45 caracteres y no se podría comparar
    -- por rango.
    `ultima_ip`       VARBINARY(16)    NULL,

    -- ⚠ Los timestamps los calcula MySQL con UTC_TIMESTAMP(3), NUNCA C#. Con dos
    --   procesos o dos zonas horarias, las fechas escritas por la aplicación
    --   dejan de ser comparables entre sí y no hay forma de arreglarlo después.
    `creada`          DATETIME(3)      NOT NULL,
    `ultimo_acceso`   DATETIME(3)      NULL,

    PRIMARY KEY (`id`),
    UNIQUE KEY `uq_cuentas_nick` (`nick`)

) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
