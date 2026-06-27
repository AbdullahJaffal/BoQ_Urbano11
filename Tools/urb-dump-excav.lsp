;;; urb-dump-excav.lsp
;;; يستخرج لكل محطة:
;;;   (1) المضلع الخام  — 4 نقاط دائمًا من STATION_INFO، قبل أي حساب للتقاطعات
;;;   (2) مضلع السيناريو — من EXCAVATION_KAZI (هو ما يُرسم فعلًا)
;;;       SCENARIO_50_50 = تقسيم 50/50
;;;       SCENARIO_UPPER = يفوز الأنبوب الأعلى
;;;       SCENARIO_LOWER = يفوز الأنبوب الأسفل
;;; النتيجة في: C:\Temp\urb_dump.txt

(vl-load-com)

(defun _all (code lst / r)
  (setq r '())
  (foreach p lst (if (= (car p) code) (setq r (append r (list (cdr p))))))
  r
)

(defun _print-poly (f dbls vtx-cnt label / i u z s)
  (setq s (strcat label " nokta=" (itoa vtx-cnt)))
  (if (> vtx-cnt 4) (setq s (strcat s "  <<< DIKKAT: " (itoa vtx-cnt) " nokta >>>")))
  (write-line s f)
  (setq i 0)
  (while (< i vtx-cnt)
    (setq u (nth (+ 1 (* i 2)) dbls))
    (setq z (nth (+ 2 (* i 2)) dbls))
    (write-line
      (strcat "    V" (itoa i)
              "  U=" (rtos u 2 4)
              "  Z=" (rtos z 2 4)) f)
    (setq i (1+ i))
  )
)

(defun c:URB-DUMP ( / net-name pipe-key sc-choice sc-key
                      boq-id aglar nid net-id pipe-id
                      f out-path
                      sta-key sta-id si-data
                      dist is-bnd si-dbls
                      excav-id sc-data dbls ints vtx-cnt)

  ;; ── مدخلات ───────────────────────────────────────────────────────────
  (setq net-name  (getstring "\nAg adi (orn. ASU): "))
  (setq pipe-key  (getstring "\nBoru key  (orn. P_N1_to_N2): "))

  ;; Keywords بحروف فقط لتجنب مشكلة الـ underscore مع AutoCAD
  (initget "Split Upper Lower")
  (setq sc-choice
    (getkword "\nSenaryo [Split=50-50 / Upper / Lower] <Split>: "))
  (cond
    ((or (not sc-choice) (= sc-choice "Split"))
     (setq sc-key "SCENARIO_50_50"))
    ((= sc-choice "Upper")
     (setq sc-key "SCENARIO_UPPER"))
    (t
     (setq sc-key "SCENARIO_LOWER"))
  )

  ;; ── ملف الإخراج ──────────────────────────────────────────────────────
  (setq out-path "C:\\Temp\\urb_dump.txt")
  (if (not (vl-file-directory-p "C:\\Temp"))
    (vl-mkdir "C:\\Temp"))
  (setq f (open out-path "w"))
  (if (not f)
    (progn (prompt "\n[HATA] Dosya acilamadi.") (exit)))

  (write-line (strcat "=== " net-name " / " pipe-key
                      " — EXCAVATION_KAZI / " sc-key " ===") f)
  (write-line "(1) HAM = STATION_INFO'daki 4-nokta poligon (Clipper2 oncesi)" f)
  (write-line (strcat "(2) SEN = " sc-key " (cizilen sey)") f)
  (write-line "<<< DIKKAT = 4'ten fazla nokta (katisma etkisi)" f)
  (write-line "" f)

  ;; ── NOD navigasyonu ───────────────────────────────────────────────────
  (setq boq-id (cdr (assoc -1 (dictsearch (namedobjdict) "URBANO_BOQ"))))
  (if (not boq-id)
    (progn (write-line "[HATA] URBANO_BOQ bulunamadi." f) (close f) (exit)))

  ;; Yeni format: AGLAR altinda; eski format: dogrudan root'ta
  (setq aglar (dictsearch boq-id "AGLAR"))
  (setq nid   (if aglar (cdr (assoc -1 aglar)) boq-id))

  (setq net-id (cdr (assoc -1 (dictsearch nid net-name))))
  (if (not net-id)
    (progn
      (write-line (strcat "[HATA] Ag yok: " net-name) f)
      (close f) (exit)))

  (setq pipe-id (cdr (assoc -1 (dictsearch net-id pipe-key))))
  (if (not pipe-id)
    (progn
      (write-line (strcat "[HATA] Boru yok: " pipe-key) f)
      (close f) (exit)))

  ;; ── İstasyonları gez ─────────────────────────────────────────────────
  (vlax-for entry (vlax-ename->vla-object pipe-id)
    (setq sta-key (vla-get-name entry))
    (if (= (substr sta-key 1 4) "STA_")
      (progn
        (setq sta-id (vlax-vla-object->ename entry))

        ;; STATION_INFO: أول (40) = المسافة؛ الـ (70) الثانية = IsCrossingBoundary
        (setq si-data (dictsearch sta-id "STATION_INFO"))
        (setq dist  (if si-data (cdr (assoc 40 si-data)) -1.0))
        (setq is-bnd
          (and si-data
               (= (nth 1 (_all 70 si-data)) 1)))

        ;; رأس السطر
        (write-line
          (strcat "[" (rtos dist 2 2) " m]"
                  (if is-bnd "  [KESISIM SINIRI]" ""))
          f)

        ;; (1) المضلع الخام — 4 نقاط في STATION_INFO
        ;; 13 scalars (40) ثم 2 flags (70) ثم 8 doubles (40) للـ ExcavPoly
        (if si-data
          (progn
            (setq si-dbls (_all 40 si-data))
            ;; indices 0-12: scalars; 13-20: ExcavPoly (4 vertices × U,Z)
            (_print-poly f
              (list 0.0                  ; dummy area placeholder
                    (nth 13 si-dbls) (nth 14 si-dbls)
                    (nth 15 si-dbls) (nth 16 si-dbls)
                    (nth 17 si-dbls) (nth 18 si-dbls)
                    (nth 19 si-dbls) (nth 20 si-dbls))
              4 "  HAM")
          )
        )

        ;; (2) السيناريو — N نقاط من EXCAVATION_KAZI
        (setq excav-id (cdr (assoc -1 (dictsearch sta-id "EXCAVATION_KAZI"))))
        (setq sc-data  (if excav-id (dictsearch excav-id sc-key) nil))
        (if sc-data
          (progn
            (setq dbls    (_all 40 sc-data))
            (setq ints    (_all 90 sc-data))
            ;; ints(0) = ring-count, ints(1) = vtx-count (exists only if ring-count>0)
            (setq vtx-cnt (if (and ints (> (nth 0 ints) 0) (cadr ints))
                            (cadr ints)
                            0))
            (if (> vtx-cnt 0)
              (_print-poly f dbls vtx-cnt "  SEN")
              (write-line "  SEN: (poligon yok - catisma yok)" f)
            )
          )
          (write-line (strcat "  SEN: (" sc-key " kaydi yok)") f)
        )

        (write-line "" f)
      )
    )
  )

  (write-line "=== Bitti ===" f)
  (close f)
  (prompt (strcat "\nKaydedildi: " out-path "\n"))
  (startapp "notepad" out-path)
  (princ)
)
