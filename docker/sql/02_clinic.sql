-- Clinic schema: the dummy "business system" the assistant talks to through Clinic.Api
CREATE SCHEMA IF NOT EXISTS clinic;

CREATE TABLE clinic.doctors (
    id                   serial PRIMARY KEY,
    tenant_id            text NOT NULL REFERENCES platform.tenants(id),
    full_name            text NOT NULL,
    specialty            text NOT NULL,
    slot_minutes         int  NOT NULL DEFAULT 20,
    consultation_fee_usd numeric(8,2) NOT NULL
);

-- weekday follows .NET DayOfWeek / Postgres DOW: 0 = Sunday ... 6 = Saturday (clinic local time)
CREATE TABLE clinic.working_hours (
    doctor_id  int      NOT NULL REFERENCES clinic.doctors(id),
    weekday    smallint NOT NULL CHECK (weekday BETWEEN 0 AND 6),
    start_time time     NOT NULL,
    end_time   time     NOT NULL CHECK (end_time > start_time),
    PRIMARY KEY (doctor_id, weekday, start_time)
);

CREATE TABLE clinic.patients (
    id         serial PRIMARY KEY,
    tenant_id  text NOT NULL REFERENCES platform.tenants(id),
    phone      text NOT NULL,                 -- WhatsApp "from" (digits only, e.g. 96170123456)
    full_name  text,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, phone)
);

CREATE TABLE clinic.appointments (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id  text NOT NULL REFERENCES platform.tenants(id),
    doctor_id  int  NOT NULL REFERENCES clinic.doctors(id),
    patient_id int  NOT NULL REFERENCES clinic.patients(id),
    starts_at  timestamptz NOT NULL,
    ends_at    timestamptz NOT NULL,
    reason     text,
    status     text NOT NULL DEFAULT 'booked'
               CHECK (status IN ('booked', 'cancelled', 'completed', 'no_show')),
    source     text NOT NULL DEFAULT 'whatsapp',
    created_at timestamptz NOT NULL DEFAULT now()
);

-- THE double-booking guarantee. Redis holds are UX; this index is correctness.
CREATE UNIQUE INDEX ux_appointments_doctor_slot
    ON clinic.appointments (doctor_id, starts_at)
    WHERE status = 'booked';

CREATE INDEX ix_appointments_patient ON clinic.appointments (tenant_id, patient_id, starts_at);
