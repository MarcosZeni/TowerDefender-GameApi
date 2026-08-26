-- =============================================================================
--  004 — Planos aprendidos
--
--  Qué recetas de fusión compró cada cuenta.
--
--      mysql -h 127.0.0.1 -P 46000 -u root -p towerdef < 004_planos_cuenta.sql
--
--  ⚠ Los planos son, junto con la billetera, LA ÚNICA PROGRESIÓN PERMANENTE del
--    proyecto. Todo lo demás —clases, nivel, perks, equipo— muere con la corrida.
--
--    Y la lista de planos es FINITA. Esa es la condición que hace tolerable la
--    ventaja del veterano: tiene techo. Por eso no se agrega equipo permanente ni
--    nada que escale sin fin, y por eso esta tabla no lleva cantidad ni nivel: un
--    plano se tiene o no se tiene.
-- =============================================================================


CREATE TABLE IF NOT EXISTS `planos_cuenta` (

    `id_cuenta`   BIGINT UNSIGNED  NOT NULL,

    -- El nombre del plano tal como está en planos.json DEL SERVIDOR.
    --
    -- ⚠ El cliente manda solo este nombre. El precio sale del JSON de acá: si lo
    --   mandara el cliente, se compraría todo a un peso.
    `plano`       VARCHAR(48)      NOT NULL,

    -- Cuánto se pagó, en el momento en que se pagó. Guardarlo acá y no leerlo del
    -- JSON al consultar es a propósito: si algún día cambia un precio, el
    -- historial no se reescribe solo.
    `precio`      INT UNSIGNED     NOT NULL,

    `comprado`    DATETIME(3)      NOT NULL,

    -- Clave compuesta: una cuenta no puede comprar dos veces el mismo plano, y
    -- el INSERT rebota solo sin necesidad de un SELECT previo. Eso también cierra
    -- la carrera de dos compras simultáneas del mismo plano.
    PRIMARY KEY (`id_cuenta`, `plano`),

    CONSTRAINT `fk_planos_cuenta`
        FOREIGN KEY (`id_cuenta`) REFERENCES `cuentas` (`id`)
        ON DELETE CASCADE

) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
