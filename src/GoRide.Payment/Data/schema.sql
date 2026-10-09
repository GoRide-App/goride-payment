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

-- SCRUM-105: the rider's email, taken from the verified identity session at checkout.
CREATE TABLE IF NOT EXISTS payment_contacts (
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin PRIMARY KEY,
    rider_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    email VARCHAR(254) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NOT NULL,
    display_name VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    captured_at DATETIME(6) NOT NULL,
    FOREIGN KEY (trip_id) REFERENCES payments(trip_id)
) ENGINE=InnoDB;

-- SCRUM-105: email receipt outbox. One row per paid trip, written in the transaction
-- that marks it paid; a background sender delivers it and records the outcome.
CREATE TABLE IF NOT EXISTS payment_receipts (
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin PRIMARY KEY,
    receipt_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL UNIQUE,
    recipient VARCHAR(254) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    recipient_name VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    status VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    attempts INT NOT NULL DEFAULT 0,
    resend_count INT NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(6) NOT NULL,
    lease_token VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
    lease_until DATETIME(6) NULL,
    provider VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NULL,
    provider_message_id VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
    last_error VARCHAR(300) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    created_at DATETIME(6) NOT NULL,
    sent_at DATETIME(6) NULL,
    last_requested_at DATETIME(6) NULL,
    INDEX ix_receipt_due (status, next_attempt_at),
    FOREIGN KEY (trip_id) REFERENCES payments(trip_id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS processed_payment_events (
    event_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin PRIMARY KEY,
    trip_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    payload_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    processed_at TIMESTAMP(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    INDEX ix_payment_events_trip (trip_id)
) ENGINE=InnoDB;

-- Demo cards a rider saved for in-app payment. Only published test card numbers are
-- accepted; the full number and CVC are never stored, only brand, last four and expiry.
CREATE TABLE IF NOT EXISTS payment_cards (
    card_id VARCHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    rider_id VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    brand VARCHAR(16) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    last4 CHAR(4) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    exp_month TINYINT UNSIGNED NOT NULL,
    exp_year SMALLINT UNSIGNED NOT NULL,
    holder_name VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci NULL,
    test_behaviour VARCHAR(24) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    fingerprint CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    is_default BOOLEAN NOT NULL DEFAULT FALSE,
    created_at DATETIME(6) NOT NULL,
    UNIQUE KEY ux_card_rider_fingerprint (rider_id, fingerprint),
    INDEX ix_card_rider (rider_id, created_at)
) ENGINE=InnoDB;
