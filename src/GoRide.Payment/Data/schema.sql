CREATE TABLE IF NOT EXISTS payments (
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin PRIMARY KEY,
    document JSON NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS payment_checkouts (
    id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    idempotency_key VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL UNIQUE,
    amount_minor BIGINT NOT NULL,
    currency CHAR(3) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    success_url TEXT NOT NULL,
    cancel_url TEXT NOT NULL,
    created_at DATETIME(6) NOT NULL,
    provider_session_id VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NULL UNIQUE,
    INDEX ix_checkout_trip (trip_id, id),
    FOREIGN KEY (trip_id) REFERENCES payments(trip_id)
) ENGINE=InnoDB;

-- One row per provider notice (payment + status). The unique key makes PayHere
-- redeliveries idempotent. Card holder name and expiry are deliberately not stored.
CREATE TABLE IF NOT EXISTS payment_verifications (
    id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    provider VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    provider_payment_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    status_code SMALLINT NOT NULL,
    order_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    amount_minor BIGINT NOT NULL,
    currency CHAR(3) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    outcome VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    payload_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    payment_method VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NULL,
    card_masked VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
    received_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_provider_notice (provider, provider_payment_id, status_code),
    INDEX ix_verification_order (order_id, id),
    INDEX ix_verification_trip (trip_id, id),
    FOREIGN KEY (trip_id) REFERENCES payments(trip_id)
) ENGINE=InnoDB;

-- SCRUM-104: the rider's in-app confirmation. Written in the same transaction that marks
-- the trip paid, so one verified card payment yields exactly one confirmation.
CREATE TABLE IF NOT EXISTS payment_confirmations (
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin PRIMARY KEY,
    -- VARCHAR, not CHAR(36): MySqlConnector maps CHAR(36) to Guid by default.
    confirmation_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL UNIQUE,
    rider_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    amount_minor BIGINT NOT NULL,
    currency CHAR(3) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    provider VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    provider_payment_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    provider_order_id VARCHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    payment_method VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NULL,
    card_masked VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
    paid_at DATETIME(6) NOT NULL,
    acknowledged_at DATETIME(6) NULL,
    INDEX ix_confirmation_rider (rider_id, paid_at),
    FOREIGN KEY (trip_id) REFERENCES payments(trip_id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS processed_payment_events (
    event_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin PRIMARY KEY,
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    payload_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    processed_at TIMESTAMP(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    INDEX ix_payment_events_trip (trip_id)
) ENGINE=InnoDB;
