using UnityEngine;
using UnityEngine.InputSystem;

/// /// <summary>
/// Contrôle les déplacements, le saut, la course et les animations d'un personnage.
/// Supporte les plateformes mobiles et une gravité personnalisée.
///
/// Conçu pour fonctionner avec Cinemachine.
/// Utilise le système d'Input Actions (PlayerInput) plutôt que du polling direct.
///
/// Prérequis : un composant PlayerInput et un Animator doivent être présents
/// sur le même GameObject, avec un Input Actions Asset correctement configuré.
///
/// @author Vahik Toroussian
/// @author modifié par Mathieu Dionne
/// @author modifié par Simon Côté
/// @version 2026
/// </summary>

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(Animator))]
[RequireComponent(typeof(PlayerInput))]
public class ControleurPersonnage : MonoBehaviour
{
    // ----------------------------------------------------------------------
    // Paramètres configurables (visibles et modifiables dans l'inspecteur)
    // ----------------------------------------------------------------------

    [Header("Vitesses")]

    [Tooltip("Vitesse normale de déplacement, en unités/seconde.")]
    public float VitesseMarche = 5f;

    [Tooltip("Vitesse de déplacement en course, en unités/seconde.")]
    public float VitesseCourse = 10f;

    [Tooltip("Vitesse de rotation du personnage, en degrés/seconde.")]
    public float VitesseRotation = 100f;

    [Header("Saut et gravité")]

    [Tooltip("Force de l'impulsion appliquée verticalement lors du saut.")]
    public float ForceSaut = 8f;

    [Tooltip("Force gravitationnelle supplémentaire appliquée au personnage (s'additionne à la gravité Unity).")]
    public float ForceGravite = 1f;

    [Header("Escaliers")]

    [Tooltip("Hauteur maximale d'un obstacle vertical que le personnage peut franchir sans sauter.")]
    public float HauteurMarcheMax = 0.4f;

    [Header("Mode de déplacement")]

    [Tooltip("Si activé, la rotation du personnage est contrôlée par le clavier (mode tank). " +
             "Si désactivé, le personnage s'oriente selon la direction de la caméra (mode TPS).")]
    public bool TourneAvecLeClavier = true;

    // ----------------------------------------------------------------------
    // Références aux actions d'entrée
    // ----------------------------------------------------------------------

    private InputAction actionDeplacer;             // Action de déplacement (Vector2) : axe avant/arrière (y) et rotation gauche/droite (x).
    private InputAction actionSauter;               // Action de saut (Button), déclenchée une seule fois par pression.
    private InputAction actionCourir;               // Action de course (Button), maintenue tant que la touche est enfoncée.

    // ----------------------------------------------------------------------
    // État courant des entrées
    // ----------------------------------------------------------------------

    private Vector2 entreeDeplacement;              // Valeur courante de l'action "Deplacer". x = rotation (-1 gauche, +1 droite), y = déplacement (-1 arrière, +1 avant).
    private bool entreeSaut;                        // Indique qu'une demande de saut est en attente de traitement dans FixedUpdate.
    private bool estEnTrainDeCourir;                // Indique si la touche de course est actuellement maintenue.

    // ----------------------------------------------------------------------
    // État interne du personnage
    // ----------------------------------------------------------------------

    private bool auSol;                             // Indique si le personnage est actuellement en contact avec le sol.
    private RaycastHit dernierHitSol;               // Résultat du dernier SphereCast de détection du sol, utilisé pour identifier la plateforme.
    private float vitesseAnimee = 0f;               // Valeur de vitesse lissée transmise à l'Animator pour les transitions d'animation.

    // ----------------------------------------------------------------------
    // Plateforme mobile
    // ----------------------------------------------------------------------

    private Transform plateformeActuelle = null;    // Référence à la plateforme actuellement sous le personnage, null si aucune.

    // ----------------------------------------------------------------------
    // Références internes aux composants
    // ----------------------------------------------------------------------

    private Rigidbody rigidbodyPersonnage;          // Référence au Rigidbody du personnage, utilisé pour tous les déplacements physiques.
    private Animator animationPersonnage;           // Référence à l'Animator du personnage, utilisé pour piloter les transitions d'animation.


    /// <summary>
    /// Initialisation des références aux composants et aux actions d'entrée.
    /// Appelée une seule fois, avant le premier Update.
    /// </summary>
    void Awake()
    {
        rigidbodyPersonnage = GetComponent<Rigidbody>();
        animationPersonnage = GetComponent<Animator>();

        PlayerInput playerInput = GetComponent<PlayerInput>();

        // Force le mode d'abonnement manuel, indépendamment de ce qui est réglé dans l'inspecteur.
        playerInput.notificationBehavior = PlayerNotifications.InvokeCSharpEvents;

        // Les noms doivent correspondre exactement (casse incluse) à ceux définis dans l'Input Actions Asset.
        actionDeplacer = playerInput.actions["Deplacer"];
        actionSauter   = playerInput.actions["Sauter"];
        actionCourir   = playerInput.actions["Courir"];
    }


    /// <summary>
    /// Abonnement aux événements d'entrée. Appelée chaque fois que l'objet est activé.
    /// </summary>
    void OnEnable()
    {
        actionSauter.performed += OnSauter;
        actionCourir.performed += OnCourirChange;
        actionCourir.canceled  += OnCourirChange;
    }


    /// <summary>
    /// Désabonnement des événements d'entrée. Appelée chaque fois que l'objet est désactivé.
    /// Essentiel pour éviter les fuites de mémoire et les appels sur un objet détruit.
    /// </summary>
    void OnDisable()
    {
        actionSauter.performed -= OnSauter;
        actionCourir.performed -= OnCourirChange;
        actionCourir.canceled  -= OnCourirChange;
    }


    /// <summary>
    /// Appelée à chaque frame de rendu. Lit la valeur de déplacement et met à jour les animations.
    /// Toute logique physique est déléguée à FixedUpdate.
    /// </summary>
    void Update()
    {
        entreeDeplacement = actionDeplacer.ReadValue<Vector2>();
        AnimerPersonnage();
    }


    /// <summary>
    /// Callback déclenché lorsque l'action "Sauter" est exécutée (pression de la touche).
    /// Enregistre la demande de saut ; le saut physique est appliqué dans FixedUpdate.
    /// </summary>
    /// <param name="contexte">Contexte fourni par le système d'Input lors du déclenchement.</param>
    private void OnSauter(InputAction.CallbackContext contexte)
    {
        entreeSaut = true;
    }


    /// <summary>
    /// Callback déclenché lorsque l'action "Courir" change d'état (touche enfoncée ou relâchée).
    /// </summary>
    /// <param name="contexte">Contexte fourni par le système d'Input ; lu comme un bouton (pressé ou non).</param>
    private void OnCourirChange(InputAction.CallbackContext contexte)
    {
        estEnTrainDeCourir = contexte.ReadValueAsButton();
    }


    /// <summary>
    /// Appelée à intervalles fixes, synchronisée avec le moteur physique.
    /// Applique le déplacement, la rotation, le saut, la gravité et le suivi de plateforme.
    /// </summary>
    void FixedUpdate()
    {
        DetecterSol();
        FranchirMarche();
        DeplaceNormal();

        // Saut : appliqué seulement si le personnage est au sol.
        if (entreeSaut && auSol)
        {
            rigidbodyPersonnage.AddForce(Vector3.up * ForceSaut, ForceMode.Impulse);
            entreeSaut = false;
        }

        // Annule toute demande de saut en attente si le personnage est en l'air.
        if (!auSol)
            entreeSaut = false;

        // Gravité supplémentaire, indépendante du framerate grâce à ForceMode.Acceleration.
        // S'additionne à la gravité native de Unity.
        rigidbodyPersonnage.AddForce(Vector3.down * ForceGravite, ForceMode.Acceleration);

        SuivrePlateforme();
    }


    /// <summary>
    /// Détecte si le personnage est au sol via un SphereCast vers le bas.
    /// Met à jour <see cref="auSol"/> et <see cref="dernierHitSol"/>.
    /// </summary>
    private void DetecterSol()
    {
        auSol = Physics.SphereCast(
            transform.position + new Vector3(0, 0.5f, 0),
            0.2f,
            Vector3.down,
            out dernierHitSol,
            0.8f
        );
    }


    /// <summary>
    /// Gère les déplacements classiques : avant/arrière et rotation clavier (mode tank).
    /// </summary>
    private void DeplaceNormal()
    {
        if (TourneAvecLeClavier)
        {
            float angleRotation = entreeDeplacement.x * VitesseRotation * Time.fixedDeltaTime;
            transform.Rotate(0, angleRotation, 0);
        }

        float vitesseActuelle = estEnTrainDeCourir
            ? entreeDeplacement.y * VitesseCourse
            : entreeDeplacement.y * VitesseMarche;

        // Met à jour le paramètre d'animation avant/arrière (1, -1 ou 0).
        float directionAvantArriere = entreeDeplacement.y > 0 ? 1f : (entreeDeplacement.y < 0 ? -1f : 0f);
        animationPersonnage.SetFloat("directionAnim", directionAvantArriere);

        // Applique la vitesse horizontale tout en conservant la vélocité verticale (gravité, saut).
        Vector3 deplacement = transform.forward * vitesseActuelle;
        rigidbodyPersonnage.linearVelocity = new Vector3(deplacement.x, rigidbodyPersonnage.linearVelocity.y, deplacement.z);
    }


    /// <summary>
    /// Met à jour les paramètres de l'Animator en fonction de l'entrée de déplacement
    /// et de l'état au sol du personnage.
    /// La vitesse d'animation est basée sur l'entrée clavier plutôt que sur la vélocité
    /// réelle du Rigidbody, pour éviter que l'animation s'arrête lorsque le personnage
    /// est bloqué contre un mur ou un obstacle.
    /// </summary>
    private void AnimerPersonnage()
    {
        // Normalise l'entrée verticale entre 0 et 1 pour piloter la transition marche/course.
        float vitesseCible = Mathf.Abs(entreeDeplacement.y) * (estEnTrainDeCourir ? 1f : 0.5f);

        // Lissage de la transition entre marche et course.
        vitesseAnimee = Mathf.Lerp(vitesseAnimee, vitesseCible, Time.deltaTime * 7f);

        animationPersonnage.SetFloat("vitesseDep", vitesseAnimee);
        animationPersonnage.SetBool("animeSaut", auSol);
    }


    /// <summary>
    /// Permet au personnage de franchir automatiquement de petites marches.
    /// Trois conditions doivent être remplies pour élever le personnage :
    /// 1. Il est au sol et se déplace à une vitesse horizontale suffisante.
    /// 2. Un obstacle de hauteur franchissable est détecté devant lui (Raycast bas).
    /// 3. Il y a de l'espace libre au-dessus de cet obstacle (Raycast haut) —
    ///    cette condition distingue une marche d'un mur ou d'un arbre, qui bloqueraient
    ///    autrement le personnage en le faisant grimper indéfiniment.
    /// </summary>
    private void FranchirMarche()
    {
        // Vérifie la vitesse horizontale réelle plutôt que la seule entrée clavier,
        // pour éviter les micro-élévations sur terrain accidenté à l'arrêt.
        Vector3 vitesseHorizontale = rigidbodyPersonnage.linearVelocity;
        vitesseHorizontale.y = 0f;

        if (!auSol || Mathf.Abs(entreeDeplacement.y) < 0.1f)
            return;

        // Raycast bas : détecte un obstacle devant le personnage.
        // Placé légèrement au-dessus du sol pour cibler les contremarches
        // plutôt que les irrégularités du sol.
        Vector3 origineBasse = transform.position + Vector3.up * 0.1f;

        if (Physics.Raycast(origineBasse, transform.forward, out RaycastHit obstacle, 0.4f))
        {
            float hauteurObstacle = obstacle.point.y - transform.position.y;

            if (hauteurObstacle > 0.05f && hauteurObstacle <= HauteurMarcheMax)
            {
                // Raycast haut : vérifie qu'il y a de l'espace libre au-dessus de l'obstacle.
                // Si ce Raycast touche quelque chose, c'est un mur ou un arbre — on n'élève pas.
                Vector3 origineHaute = transform.position + Vector3.up * (HauteurMarcheMax + 0.2f);

                if (!Physics.Raycast(origineHaute, transform.forward, 0.4f))
                {
                    rigidbodyPersonnage.MovePosition(
                        rigidbodyPersonnage.position + Vector3.up * hauteurObstacle
                    );
                }
            }
        }
    }


    /// <summary>
    /// Gère le suivi d'une plateforme mobile en parentant temporairement le personnage à elle.
    /// Unity applique automatiquement le déplacement de la plateforme au personnage via le parentage,
    /// sans décalage d'une frame.
    ///
    /// Prérequis : les plateformes mobiles doivent être créées avec ProBuilder pour garantir
    /// un Scale (1, 1, 1). Un Scale différent se transmettrait au personnage via le parentage
    /// et provoquerait une déformation visuelle.
    /// </summary>
    private void SuivrePlateforme()
    {
        if (auSol
            && dernierHitSol.collider != null
            && dernierHitSol.collider.tag == "PlateformeMouvante")
        {
            Transform nouvellePlateforme = dernierHitSol.collider.transform;

            // Si le personnage vient de monter sur une nouvelle plateforme, on le parenté à elle.
            // worldPositionStays: true conserve la position absolue du personnage dans la scène.
            if (nouvellePlateforme != plateformeActuelle)
            {
                plateformeActuelle = nouvellePlateforme;
                transform.SetParent(plateformeActuelle, worldPositionStays: true);
            }
        }
        else if (plateformeActuelle != null)
        {
            // Le personnage a quitté la plateforme : on le détache de la hiérarchie.
            // worldPositionStays: true conserve sa position absolue dans la scène.
            transform.SetParent(null, worldPositionStays: true);
            plateformeActuelle = null;
        }
    }


    // ----------------------------------------------------------------------
    // API publique — appelée par des scripts externes (ex. ChangeCameraVirtuelle)
    // ----------------------------------------------------------------------

    /// <summary>
    /// Configure le mode de déplacement du personnage selon la caméra active.
    /// Appelée par <see cref="ChangeCameraVirtuelle"/> lors d'un changement de caméra.
    /// </summary>
    /// <param name="orienteParCamera">
    /// True : le personnage s'oriente selon la direction de la caméra (mode TPS / FPS).
    /// False : la rotation est contrôlée par le clavier (mode tank).
    /// </param>
    public void SetModeCamera(bool orienteParCamera)
    {
        TourneAvecLeClavier = !orienteParCamera;
    }
}