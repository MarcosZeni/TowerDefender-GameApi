-- =============================================================================
--  003 — Corridas
--
--  Una fila por corrida abierta. Es la tabla del anticheat: lo que el cliente
--  reporta entra acá como un RECLAMO, no como un hecho.
--
--      mysql -h 127.0.0.1 -P 46000 -u root -p towerdef < 003_runs.sql
--
--  ⚠ SIN columna `expira`, y es deliberado.
--
--    Una corrida son 100 oleadas y puede durar horas. El contrato original tenía
--    un vencimiento de 90 minutos, escrito pensando en partidas de veinte:
--    rechazar un finish después de tres horas bien jugadas es lo peor que puede
--    hacer este servidor.
--
--    Lo que reemplaza al vencimiento es UNA SOLA CORRIDA ABIERTA POR CUENTA:
--    abrir una nueva cierra la anterior como 'abandono'. Así no hace falta
--    ningún proceso de limpieza y "la cierra el usuario" queda cubierto solo.
-- =============================================================================


CREATE TABLE IF NOT EXISTS `runs` (

    `id`              BIGINT UNSIGNED  NOT NULL AUTO_INCREMENT,
    `id_cuenta`       BIGINT UNSIGNED  NOT NULL,

    -- -------------------------------------------------------------------------
    --  La seed
    -- -------------------------------------------------------------------------
    -- ⚠ La genera el SERVIDOR, siempre. Si la eligiera el cliente, un tramposo
    --   podría probar semillas hasta encontrar una fácil y después reclamar la
    --   corrida como buena. Acá no hay tirada floja que buscar.
    --
    -- Se guarda para poder REVALIDAR: con la seed y el log de acciones, el
    -- servidor re-simula la corrida entera y compara.
    `seed`            BIGINT UNSIGNED  NOT NULL,

    -- -------------------------------------------------------------------------
    --  Lo que reclama el cliente
    -- -------------------------------------------------------------------------

    -- 1 solo si llegó a la oleada 100 y mató al jefe final.
    -- ⚠ El servidor lo verifica contra `oleada`: no puede ser 1 con oleada < 100.
    `gano`            TINYINT UNSIGNED NOT NULL DEFAULT 0,

    -- De 1 a 100. Las oleadas se numeran CORRIDAS, no por área.
    `oleada`          TINYINT UNSIGNED NOT NULL DEFAULT 0,

    -- Solo para mostrar. El ranking NO lo usa: se deriva de la oleada.
    `area`            TINYINT UNSIGNED NOT NULL DEFAULT 1,

    -- Ticks de simulación. Es el desempate del ranking: menos es mejor.
    -- INT alcanza de sobra — una corrida de tres horas son ~324.000 ticks.
    `ticks`           INT UNSIGNED     NOT NULL DEFAULT 0,

    -- -------------------------------------------------------------------------
    --  Cómo terminó
    -- -------------------------------------------------------------------------
    -- 'abandono' es la que pone /run/start al abrir otra corrida encima.
    `motivo_cierre`   ENUM('perdio','gano','abandono') NULL,

    -- -------------------------------------------------------------------------
    --  La temporada
    -- -------------------------------------------------------------------------
    -- ⚠ Los JSON de Client\Datos\ son ENTRADA de la simulación. Cambiar un número
    --   de balance vuelve irreplicables las corridas viejas: la misma seed y el
    --   mismo log dan otro resultado.
    --
    --   Por eso cada rebalanceo es una TEMPORADA del ranking, y esta columna es
    --   la que las separa. Va primera en el índice de la tabla de posiciones para
    --   que las temporadas viejas sigan consultables.
    `datos_version`   SMALLINT UNSIGNED NOT NULL DEFAULT 1,

    -- -------------------------------------------------------------------------
    --  El log de acciones
    -- -------------------------------------------------------------------------
    -- ⚠ Guarda DECISIONES, no resultados. Lo que el RNG sembrado determina —qué
    --   NPC sale con perk, qué ítem suelta, qué hay en la tienda, qué 3 perks se
    --   ofrecen— el servidor lo regenera de la seed. Guardar resultados infla un
    --   log que ya es grande con 100 oleadas, y no aporta nada: el servidor no le
    --   creería al cliente igual.
    --
    -- MEDIUMBLOB son 16 MB. Comprimido, una corrida de 100 oleadas tendría que
    -- entrar cómoda; `log_truncado` avisa si no entró.
    `log_formato`     TINYINT UNSIGNED NOT NULL DEFAULT 1,
    `log_truncado`    TINYINT UNSIGNED NOT NULL DEFAULT 0,
    `log_acciones`    MEDIUMBLOB       NULL,

    -- -------------------------------------------------------------------------
    --  Validación
    -- -------------------------------------------------------------------------
    -- ⚠ Arranca en 0 SIEMPRE. Lo que manda el cliente es un reclamo.
    --
    --   No hace falta revalidar desde el día uno; SÍ hace falta guardar desde el
    --   día uno lo necesario para poder hacerlo después, porque lo que no se
    --   escribió hoy no se agrega mañana.
    `validado`        TINYINT UNSIGNED NOT NULL DEFAULT 0,

    `iniciada`        DATETIME(3)      NOT NULL,
    `terminada`       DATETIME(3)      NULL,

    PRIMARY KEY (`id`),

    -- Para encontrar la corrida abierta de una cuenta, que es lo que consulta
    -- /run/start antes de abrir otra.
    KEY `idx_runs_cuenta_abierta` (`id_cuenta`, `motivo_cierre`),

    -- ⚠ El índice del ranking, en el ORDEN EXACTO de la consulta:
    --     WHERE datos_version = ? AND motivo_cierre IS NOT NULL
    --     ORDER BY gano DESC, oleada DESC, ticks ASC
    --
    --   Arriba los que ganaron y entre ellos el más rápido; abajo los que no,
    --   por distancia. Con el tiempo la punta pasa a ser una tabla de velocidad,
    --   y por eso existe adelantar oleada: sin esa mecánica el desempate por
    --   ticks no mide nada.
    KEY `idx_runs_ranking` (`datos_version`, `gano`, `oleada`, `ticks`),

    CONSTRAINT `fk_runs_cuenta`
        FOREIGN KEY (`id_cuenta`) REFERENCES `cuentas` (`id`)
        ON DELETE CASCADE

) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
