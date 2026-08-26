-- =============================================================================
--  005 — Intentos de login
--
--  Una fila por intento. Es lo que hace que el freno se ACUERDE.
--
--      mysql -h 127.0.0.1 -P 46000 -u root -p towerdef < 005_intentos_login.sql
--
--  Por qué hace falta, si ya hay un rate limiter en memoria:
--
--    El limitador de Kestrel es instantáneo y no toca la base, que es lo que
--    hace falta bajo una avalancha. Lo que NO puede hacer es acordarse: se
--    reinicia la API y se olvida de todo —un atacante paciente espera un
--    deploy— y con dos instancias cada una cuenta por su lado.
--
--    Y hay un ataque que el limitador por IP no ve en absoluto: una botnet
--    repartida, mil IPs con un intento cada una, ninguna llega a su límite y
--    todas apuntan al mismo nick. Eso solo se detecta contando por NICK, y
--    contar por nick exige acordarse.
--
--  ⚠ Se cuentan SOLO los fallos, y un acierto reinicia la cuenta. Si contara
--    todos los intentos, cualquiera podría dejar afuera a otro tirándole logins
--    a su nombre. El arma existe igual en chico —15 contraseñas mal y el dueño
--    espera la ventana— y por eso la ventana es corta y el número generoso: es
--    un freno, no un bloqueo de cuenta.
-- =============================================================================


CREATE TABLE IF NOT EXISTS `intentos_login` (

    `id`        BIGINT UNSIGNED  NOT NULL AUTO_INCREMENT,

    -- VARBINARY(16) entra un IPv6 completo. Un IPv4 se guarda en 4 bytes, y
    -- SIEMPRE normalizado: la misma IP puede llegar como 1.2.3.4 o como
    -- ::ffff:1.2.3.4 según cómo esté el socket, y sin normalizar contaría doble.
    `ip`        VARBINARY(16)    NULL,

    -- El nick que se intentó, exista o no la cuenta. NULL cuando el pedido se
    -- rechazó antes de leer el cuerpo —por ejemplo, por rate limit—: deserializar
    -- el JSON del que ya se pasó del límite es gastar trabajo justo en quien
    -- menos se lo merece.
    `nick`      VARCHAR(20)      NULL,

    -- 0 = falló, 1 = entró. Un acierto reinicia la cuenta de fallos del nick.
    `exito`     TINYINT UNSIGNED NOT NULL DEFAULT 0,

    `cuando`    DATETIME(3)      NOT NULL,

    PRIMARY KEY (`id`),

    -- Los dos índices son exactamente las dos consultas del freno, en el orden
    -- en que filtran: primero la columna de igualdad, después la de rango.
    KEY `idx_intentos_ip` (`ip`, `cuando`),
    KEY `idx_intentos_nick` (`nick`, `exito`, `cuando`),

    -- Para el barrido por antigüedad.
    KEY `idx_intentos_cuando` (`cuando`)

) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
