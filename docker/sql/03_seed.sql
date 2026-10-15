-- Seed data: 2 clinics, 4 doctors, weekly schedules (fictional people)
INSERT INTO clinic.doctors (tenant_id, full_name, specialty, slot_minutes, consultation_fee_usd) VALUES
('clinic-haddad', 'Dr. Karim Haddad', 'Cardiology',                      20, 60.00),   -- id 1
('clinic-haddad', 'Dr. Rania Khoury', 'Cardiology - Echocardiography',   30, 50.00),   -- id 2
('smile-dental',  'Dr. Nadim Saab',   'General Dentistry',               30, 40.00),   -- id 3
('smile-dental',  'Dr. Lea Mansour',  'Orthodontics',                    30, 50.00);   -- id 4

-- Dr. Haddad: Mon-Fri 09:00-13:00 and Mon/Wed 15:00-18:00, Sat 09:00-12:00
INSERT INTO clinic.working_hours (doctor_id, weekday, start_time, end_time)
SELECT 1, d, '09:00', '13:00' FROM generate_series(1, 5) AS d;
INSERT INTO clinic.working_hours VALUES (1, 1, '15:00', '18:00'), (1, 3, '15:00', '18:00'), (1, 6, '09:00', '12:00');

-- Dr. Khoury: Tue/Thu 10:00-16:00
INSERT INTO clinic.working_hours VALUES (2, 2, '10:00', '16:00'), (2, 4, '10:00', '16:00');

-- Smile Dental: Mon-Sat 10:00-18:00 (Saab), Tue/Fri 12:00-19:00 (Mansour)
INSERT INTO clinic.working_hours (doctor_id, weekday, start_time, end_time)
SELECT 3, d, '10:00', '18:00' FROM generate_series(1, 6) AS d;
INSERT INTO clinic.working_hours VALUES (4, 2, '12:00', '19:00'), (4, 5, '12:00', '19:00');
